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
        private readonly Timer timer = new Timer { Interval = 2000 };
        private bool busy;
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

            var menu = new ContextMenuStrip();
            menu.Items.Add(whatIsHere);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Crucible", null, (s, e) => OpenWebsite());
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Upload now", null, (s, e) => { forceNext = true; });
            menu.Items.Add(pauseItem);
            menu.Items.Add("Settings...", null, (s, e) => ShowSettings());
            menu.Items.Add("Reinstall the addon from GitHub", null, async (s, e) => await ReinstallAddon());
            menu.Items.Add("View log", null, (s, e) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            pauseItem.Click += (s, e) => TogglePause();
            updateItem.Click += (s, e) => OpenDownloadPage();

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
            timer.Tick += async (s, e) => await Tick();
            timer.Start();
            forceNext = true; // the first pass fetches whatever the clock says
        }

        private async Task Tick()
        {
            if (busy) return;
            busy = true;
            try
            {
                bool force = forceNext;
                forceNext = false;
                CycleReport report = await Task.Run(() => cycle.RunAsync(force));
                Show(report.State, report.Reason);
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
            }
        }

        private void Show(TrayState newState, string newReason)
        {
            state = newState;
            reason = newReason;
            // M112: a newer tray app on offer adds a badge to whatever colour the state is.
            icon.Icon = Icons.For(state, nudge.Available != null);
            pauseItem.Text = config.Paused ? "Resume" : "Pause";
            string? addon = AddonVersionHere();
            whatIsHere.Text = AppInfo.Name + " " + AppInfo.Version + (addon == null ? "" : "  ·  addon " + addon);
            icon.Text = TrayText.Tooltip(state, reason, cycle.LastUploadUtc, cycle.PricesAtUtc, nudge.TooltipLine, DateTime.UtcNow);
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
