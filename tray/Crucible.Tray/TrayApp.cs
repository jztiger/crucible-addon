using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Crucible.Tray
{
    /// <summary>
    /// The resident part: a tray icon that is always visible while this runs, a menu, and a two-second timer that
    /// asks the core for one pass. It never runs unseen: if the icon cannot be shown, it exits.
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly TrayConfig config;
        private readonly ISecretProtector protector;
        private readonly TrayLog log;
        private readonly ServerClient client;
        private readonly Cycle cycle;
        private readonly NotifyIcon icon;
        /// <summary>M112: whether a newer tray app is on offer, and whether its one balloon is due.</summary>
        private readonly UpdateNudge nudge;
        private readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem("Pause");
        private readonly ToolStripMenuItem updateItem = new ToolStripMenuItem("") { Visible = false };
        /// <summary>What is installed, at the top of the menu: the tooltip has no room for it.</summary>
        private readonly ToolStripMenuItem whatIsHere = new ToolStripMenuItem("") { Enabled = false };
        /// <summary>Ruling S-R12: Crucible Canvas's status words, under <see cref="whatIsHere"/> while Canvas is on - the
        /// 63-character tooltip has no room for them.</summary>
        private readonly ToolStripMenuItem canvasHere = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        private readonly Timer timer = new Timer { Interval = 2000 };
        /// <summary>Crucible Canvas (spec 2026-10-15 §6): the WoW window finder and mover, driven on the UI thread only - by
        /// the timer's tick and the menu's clicks, never from inside Task.Run.</summary>
        private readonly WindowsWindowOps windowOps = new WindowsWindowOps();
        private readonly SpanController span;
        private readonly ToolStripMenuItem spanItem = new ToolStripMenuItem("Span WoW now");
        private readonly ToolStripMenuItem restoreItem = new ToolStripMenuItem("Restore window");
        private bool busy;
        /// <summary>The once-only notice (spec §8) is due: recorded and saved in the constructor, shown by the first Tick.</summary>
        private bool noticePending;
        private bool forceNext;
        private DateTime versionCheckedUtc = DateTime.MinValue;
        private TrayState state = TrayState.Ok;
        private string reason = "";
        /// <summary>Which balloon is up: a click on the update one opens the download page.</summary>
        private bool balloonIsUpdate;
        /// <summary>Spec 2026-10-12: the version this process replaced, when it was started by the swap; null otherwise.</summary>
        private readonly string? updatedFrom;

        public TrayApp(TrayConfig config, ISecretProtector protector, TrayLog log, string? updatedFrom = null)
        {
            this.config = config;
            this.protector = protector;
            this.log = log;
            this.updatedFrom = updatedFrom;
            client = new ServerClient(config, ServerClient.CreateHandler());
            cycle = new Cycle(config, client, new SentLog(Paths.Sent), new Stability(), log, () => DateTime.UtcNow, Paths.Exe, new Authenticode());
            nudge = new UpdateNudge(AppInfo.Version, config.NudgedVersion);
            span = new SpanController(windowOps, () => DateTime.UtcNow, log);   // before the first Show, which reads its status
            span.Spanned += WriteTopology;

            var menu = new ContextMenuStrip();
            menu.Items.Add(whatIsHere);
            menu.Items.Add(canvasHere);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Crucible", null, (s, e) => OpenWebsite());
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Upload now", null, (s, e) => { forceNext = true; });
            menu.Items.Add(pauseItem);
            menu.Items.Add("Settings...", null, (s, e) => ShowSettings());
            menu.Items.Add("Displays...", null, (s, e) => ShowDisplays());
            menu.Items.Add(spanItem);
            menu.Items.Add(restoreItem);
            menu.Items.Add("Reinstall the addon from GitHub", null, async (s, e) => await ReinstallAddon());
            menu.Items.Add("View log", null, (s, e) => OpenLog());
            menu.Items.Add("View notice", null, (s, e) => Guarded("the notice window", () => { using (var n = new NoticeForm(SetupForm.NoticeText(), false)) n.ShowDialog(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            pauseItem.Click += (s, e) => TogglePause();
            updateItem.Click += (s, e) => OpenDownloadPage();
            spanItem.Click += (s, e) => Guarded("span", () => Show(state, reason, span.SpanNow(config)));
            restoreItem.Click += (s, e) => Guarded("restore", () => Show(state, reason, span.RestoreNow()));

            icon = new NotifyIcon { ContextMenuStrip = menu, Icon = Icons.For(config.Paused ? TrayState.Paused : TrayState.Ok), Text = AppInfo.Name, Visible = true };
            icon.DoubleClick += (s, e) => OpenWebsite();
            icon.BalloonTipClicked += (s, e) => { if (balloonIsUpdate) OpenDownloadPage(); };
            if (!icon.Visible) throw new InvalidOperationException("the tray icon could not be shown");

            Show(config.Paused ? TrayState.Paused : TrayState.Ok, "");
            log.Write("started " + AppInfo.Name + " tray " + AppInfo.Version);
            if (updatedFrom != null)
            {
                log.Write("updated from " + updatedFrom + " to " + AppInfo.Version); // two checked version numbers
                balloonIsUpdate = false;
                icon.ShowBalloonTip(5000, AppInfo.Name, "Crucible is now version " + AppInfo.Version + ".", ToolTipIcon.Info);
            }
            // Spec §8: a notice version carried over by acceptance is shown once, to read, with Close - then recorded.
            int current = Notice.VersionOf(SetupForm.NoticeText());
            if (Notice.NeedsShowing(config.NoticeShownVersion, current) && Notice.IsAccepted(config.AcceptedNoticeVersion, current))
            {
                config.NoticeShownVersion = current;
                Save(); // recorded BEFORE the window, so a crash or a kill never shows it twice
                // Shown by the first Tick, once the message loop runs (task 4 review): a modal window here, in the
                // constructor, would let Quit's ExitThread go unheard and leave the tray running with no icon.
                noticePending = true;
            }
            timer.Tick += async (s, e) => await Tick();
            timer.Start();
            forceNext = true; // the first pass fetches whatever the clock says
        }

        private async Task Tick()
        {
            if (noticePending)
            {
                // Spec §8 rule (1), ruling S-R20: nothing is blocked, the cycle keeps running. Cleared before the window, which
                // is shown above the busy guard: the timer keeps ticking under it and those ticks run their passes, as they do
                // under Settings and Displays (the awaits resume through the dialog's message loop).
                noticePending = false;
                log.Write("showing the notice once");
                Guarded("the notice window", () => { using (var n = new NoticeForm(SetupForm.NoticeText(), true)) n.ShowDialog(); });
                if (!timer.Enabled) return;   // Quit (or a swap) happened while it was up
            }
            if (busy) return;
            busy = true;
            try
            {
                bool force = forceNext;
                forceNext = false;
                CycleReport report = await Task.Run(() => cycle.RunAsync(force));
                // Back on the UI thread, after the pass. A Canvas fault is its own: it must not hide what the pass reported.
                SpanStatus canvas;
                try { canvas = span.Tick(config); }
                catch (Exception e) { log.Write("the canvas tick failed: " + e.GetType().Name); canvas = span.Status; }
                Show(report.State, report.Reason, canvas);
                // Ruling P2-R6: an install swapped the Canvas folder, so the current span's Topology.lua is written again
                // (by FlushTopology once this pass has ended).
                if (report.CanvasAddonInstalled != null && canvas.State == SpanState.Spanned && canvas.Plan != null) pendingTopology = canvas.Plan;
                if (report.ProductsChanged) Save(); // the last list the server sent, for the next start and for offline
                if (report.AddonInstalled != null)
                {
                    balloonIsUpdate = false;
                    icon.ShowBalloonTip(5000, AppInfo.Name, "The Crucible addon is now version " + report.AddonInstalled + ".", ToolTipIcon.Info);
                }

                // Spec 2026-10-12: the old copy of this exe goes after the first clean pass of the new one.
                if (updatedFrom != null && report.State == TrayState.Ok) ForgetOldCopy();
                if (report.TrayUpdateFailed != null) Save(); // the failed version, so it is not tried again after a restart
                // A verified newer tray waits beside the exe: swap it in after a pass that moved nothing.
                if (cycle.StagedVersion != null && SelfUpdate.QuietEnough(report, config.Paused) && TrySwap(cycle.StagedVersion)) return;
                // An update that failed or stalled is told at once, not at the next daily check (review I4b).
                if (report.TrayUpdateFailed != null || cycle.TrayStalled) versionCheckedUtc = DateTime.MinValue;

                if (!config.Paused && DateTime.UtcNow - versionCheckedUtc > TimeSpan.FromHours(24))
                {
                    versionCheckedUtc = DateTime.UtcNow;
                    string? offered = await Task.Run(() => client.LatestVersionAsync());
                    // M112's nudge is now the fallback (spec 2026-10-12): a signed build fetches the update itself, so
                    // the person is only sent to the download page by an unsigned build, for a version that failed, or
                    // for one that three checks could not get. The core says which (review I1, I4).
                    bool signedBuild = await Task.Run(() => cycle.SelfSigner != null);
                    string? toNudge = SelfUpdate.NudgeFor(offered, config.FailedUpdateVersion, signedBuild, cycle.TrayStalled);
                    if (offered != null && toNudge == null) nudge.Withdraw();
                    bool balloon = nudge.Offer(toNudge);
                    updateItem.Text = nudge.MenuText ?? "";
                    updateItem.Visible = nudge.Available != null;
                    if (balloon)
                    {
                        config.NudgedVersion = nudge.Nudged ?? "";
                        Save(); // once per new version, across restarts too
                        balloonIsUpdate = true;
                        icon.ShowBalloonTip(10000, AppInfo.Name, nudge.BalloonText ?? "", ToolTipIcon.Info);
                        log.Write("tray " + nudge.Available + " is on offer"); // a checked version number: safe to log
                    }
                    Show(state, reason); // the tooltip's first line and the badge
                }
            }
            catch (Exception e)
            {
                log.Write("a pass failed: " + e.GetType().Name);
                Show(TrayState.Retrying, "Something went wrong - see the log");
            }
            finally
            {
                busy = false;
                // The pass has ended (whichever way Tick left): a Topology.lua queued during it goes out now. Guarded:
                // nothing may escape the timer's async handler.
                Guarded("topology", FlushTopology);
            }
        }

        private void Show(TrayState newState, string newReason, SpanStatus? canvas = null)
        {
            state = newState;
            reason = newReason;
            // M112: a newer tray app on offer adds a badge to whatever colour the state is.
            icon.Icon = Icons.For(state, nudge.Available != null);
            pauseItem.Text = config.Paused ? "Resume" : "Pause";
            string? addon = AddonVersionHere();
            string? canvasAddon = AddonVersionHere(AddonSpec.Canvas);
            whatIsHere.Text = AppInfo.Name + " " + AppInfo.Version + (addon == null ? "" : "  ·  addon " + addon) + "  ·  canvas " + (canvasAddon ?? "not installed");
            // Ruling S-R12: the Canvas status words live here (and in the Displays window), not in the tooltip.
            canvasHere.Text = config.CanvasEnabled ? "Canvas: " + (canvas ?? span.Status).Words : "";
            canvasHere.Visible = config.CanvasEnabled;
            spanItem.Enabled = restoreItem.Enabled = config.CanvasEnabled;
            icon.Text = TrayText.Tooltip(state, reason, cycle.LastUploadUtc, cycle.PricesAtUtc, nudge.TooltipLine, DateTime.UtcNow);
        }

        /// <summary>
        /// Spec §4.1: after a verified span, the exact rectangles go into every installed Canvas folder. Ruling P2-R6:
        /// never while a pass is running (an install swaps the folder away, taking a fresh Topology.lua with it) - the
        /// write waits for the pass to end - and it is repeated after any pass that installed the Canvas addon.
        /// </summary>
        private PlanResult? pendingTopology;
        private void WriteTopology(PlanResult plan, IntPtr handle)
        {
            Guarded("topology", () =>
            {
                pendingTopology = plan;
                if (busy) return; // Tick writes it after the pass
                FlushTopology();
            });
        }

        /// <summary>Called at the end of every Tick (after the pass) and by WriteTopology when no pass is running.</summary>
        private void FlushTopology()
        {
            PlanResult? plan = pendingTopology;
            if (plan == null) return;
            pendingTopology = null;
            string text = TopologyFile.Render(plan, AppInfo.Version, DateTime.UtcNow);
            foreach (string folder in GameFolders.AddonFolders(config.WowFolder, config.Products, AddonSpec.Canvas))
            {
                try { AtomicFile.Write(Path.Combine(folder, TopologyFile.Name), TopologyFile.Bytes(text)); log.Write("wrote Topology.lua (" + plan.Window.Width + "x" + plan.Window.Height + ")"); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { log.Write("could not write Topology.lua: " + e.GetType().Name); }
            }
        }

        /// <summary>
        /// The Displays window, from the menu and from Settings' "Displays..." button. Built and shown here, synchronously,
        /// and guarded as a whole: its constructor reads the displays and could throw.
        /// </summary>
        private void ShowDisplays()
        {
            try
            {
                using (var form = new DisplaysForm(config, windowOps, () => span.Status, () => span.SpanNow(config), () => span.RestoreNow(), log.Write))
                {
                    // Ruling S-R10: "Span WoW now" inside the window counts as accepting the choice - the form applied the
                    // (validated) choice to this config before the span - so a span survives a restart even after Cancel.
                    // Only OK copies the window's fields: on Cancel they hold edits the person cancelled, made after the
                    // span and never validated (task 4 review), and only the choice the span already applied is saved.
                    bool ok = form.ShowDialog() == DialogResult.OK;
                    if (ok) form.ApplyTo(config);
                    else if (!form.ChoiceApplied) return;
                }
                Save();
                log.Write("displays: canvas " + (config.CanvasEnabled ? "on" : "off") + ", " + config.CanvasMode + ", auto " + config.CanvasAutoSpan); // no ids: a device id can name a model, nothing more, but keep the log to words
                forceNext = true;
                Show(state, reason);
            }
            catch (Exception e)
            {
                // Guarded, from the menu and from Settings' button alike: an exception escaping a click handler would end the tray.
                log.Write("the Displays window failed: " + e.GetType().Name);
                try { MessageBox.Show("The Displays window could not open (" + e.GetType().Name + ").", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                catch (Exception m) { log.Write("could not say so: " + m.GetType().Name); }
            }
        }

        /// <summary>Runs a menu or event handler so that nothing escapes it (an exception out of a handler would end the
        /// tray): the log gets the exception's type name only.</summary>
        private void Guarded(string what, Action handler)
        {
            try { handler(); }
            catch (Exception e) { log.Write(what + " failed: " + e.GetType().Name); }
        }

        /// <summary>The real file operations behind <see cref="SelfUpdate.Swap"/>.</summary>
        private sealed class WindowsFiles : IFileOps
        {
            public bool Exists(string path) => File.Exists(path);
            public void Delete(string path) => File.Delete(path);
            public void Move(string from, string to) => File.Move(from, to);
            public void Start(string exe) => Process.Start(new ProcessStartInfo(exe, "--after-update " + AppInfo.Version) { UseShellExecute = false });
        }

        /// <summary>
        /// The swap (spec 2026-10-12, section 5), carried out by the core over real files: the running exe is renamed to
        /// .old, the staged file takes its name, the new one is started and this one leaves. True when this process is
        /// on its way out. Anything else puts the running build back (review I2) and lets the nudge take over.
        /// </summary>
        private bool TrySwap(string version)
        {
            UpdatePaths? paths = cycle.UpdatePaths;
            if (paths == null) return false;
            log.Write("swapping to tray " + version);
            SwapResult result = SelfUpdate.Swap(paths, new WindowsFiles());
            if (result == SwapResult.Swapped)
            {
                timer.Stop();
                icon.Visible = false;
                ExitThread();
                return true;
            }
            log.Write("tray " + version + " could not be swapped in: " + (result == SwapResult.Restored ? "this build is back in place" : "this build is NOT back under its own name - look beside Crucible.exe for .old or .new and rename it"));
            config.FailedUpdateVersion = version;
            Save();
            cycle.ForgetStaged();
            return false;
        }

        private void ForgetOldCopy()
        {
            UpdatePaths? paths = cycle.UpdatePaths;
            if (paths == null || !File.Exists(paths.Old)) return;
            try { File.Delete(paths.Old); log.Write("the old copy is gone"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { } // still in use for a moment: next pass
        }

        private void TogglePause()
        {
            config.Paused = !config.Paused;
            Save();
            log.Write(config.Paused ? "paused" : "resumed");
            forceNext = !config.Paused;
            Show(config.Paused ? TrayState.Paused : TrayState.Ok, config.Paused ? "Paused" : "");
        }

        private void ShowSettings()
        {
            using (var form = new SetupForm(config, false))
            {
                form.DisplaysRequested += () => ShowDisplays();
                if (form.ShowDialog() != DialogResult.OK) return;
                form.ApplyTo(config);
                if (form.NewCredentials != null)
                {
                    // In place: the client and the pass read this object at their next request.
                    ConfigStore.ApplyCredentials(config, form.NewCredentials);
                    log.Hide(config.ClientSecret, config.UploadKey);
                    log.Write("new credentials loaded");
                }
            }
            Save();
            try { Autostart.Apply(config.StartWithWindows); }
            catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException || e is IOException)
            {
                log.Write("could not change Start with Windows: " + e.GetType().Name);
            }
            forceNext = true;
        }

        private void Save()
        {
            try { ConfigStore.Save(Paths.Config, config, protector); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log.Write("could not save the settings: " + e.GetType().Name);
            }
        }

        private void OpenWebsite() { Start(config.Ui); }

        /// <summary>The website's download page, from the person's own settings file - never an address the server sent.</summary>
        private void OpenDownloadPage() { Start(UpdateNudge.DownloadPage(config.Ui)); }

        private void OpenLog()
        {
            if (!File.Exists(log.FilePath)) log.Write("the log was opened");
            Start(log.FilePath);
        }

        /// <summary>Hands a website address or the log file to Windows to open. Nothing else is ever started.</summary>
        private void Start(string what)
        {
            try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is FileNotFoundException)
            {
                log.Write("could not open it: " + e.GetType().Name);
            }
        }

        /// <summary>The version installed in the game folder, or null when the addon is not there.</summary>
        private string? AddonVersionHere()
        {
            foreach (string folder in GameFolders.AddonFolders(config.WowFolder, config.Products))
            {
                string? v = AddonInstaller.InstalledVersion(folder);
                if (v != null) return v;
            }
            return null;
        }

        /// <summary>The same for another addon of ours (ruling S-R12: the Crucible Canvas addon, for the menu's top line).</summary>
        private string? AddonVersionHere(AddonSpec spec)
        {
            foreach (string folder in GameFolders.AddonFolders(config.WowFolder, config.Products, spec))
            {
                string? v = AddonInstaller.InstalledVersion(folder, spec);
                if (v != null) return v;
            }
            return null;
        }

        /// <summary>
        /// Puts the addon back from the code published for anyone to read, WITHOUT asking our server. This is the
        /// way out if the server ever served something wrong, so it deliberately goes straight to GitHub.
        /// </summary>
        private async Task ReinstallAddon()
        {
            DialogResult go = MessageBox.Show(
                "This replaces the Crucible addon with the code published on GitHub, without asking the server.\n\n"
                    + "Your own prices are kept. Carry on?",
                AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (go != DialogResult.OK) return;

            try
            {
                AddonManifest? published;
                using (HttpClientHandler handler = ServerClient.CreateHandler())
                {
                    published = await Task.Run(() => PublicAddon.FetchAsync(handler));
                }
                if (published == null)
                {
                    log.Write("the published addon could not be read");
                    MessageBox.Show("The published addon could not be read just now. Try again later.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var folders = new List<string>(GameFolders.AddonFolders(config.WowFolder, config.Products));
                if (folders.Count == 0)
                {
                    string? fresh = GameFolders.InstallTarget(config.WowFolder, config.Products);
                    if (fresh != null) folders.Add(fresh);
                }
                if (folders.Count == 0)
                {
                    MessageBox.Show("None of the Forever game folders (" + string.Join(", ", config.Products) + ") is here to install the addon into.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                foreach (string folder in folders) log.Write("from GitHub: " + AddonInstaller.Install(folder, published));
                Show(state, reason);
                MessageBox.Show("The addon is back, at version " + published.Version + ".", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                log.Write("the reinstall failed: " + e.GetType().Name);
                MessageBox.Show("The addon could not be put back: " + e.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Quit()
        {
            timer.Stop();
            icon.Visible = false;
            log.Write("quit");
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                icon.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
