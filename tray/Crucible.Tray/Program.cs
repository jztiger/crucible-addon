using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Crucible.Tray
{
    internal static class Program
    {
        /// <summary>
        /// No arguments: the tray app. Three headless modes, each of which does its one thing and exits - a
        /// windowed program has no console, so each also writes what it says to the file named with --out:
        ///   --version  [--out f]                          what this build is
        ///   --selftest [--out f]                          proves the Windows-only parts work on this PC (used by CI)
        ///   --once --config f --wow folder [--out f]      one send-and-fetch pass with a plain download file, then exit
        ///                                                 (end-to-end tests; never touches %APPDATA% or the Run key)
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            Dictionary<string, string> opts = Options(args);
            if (opts.ContainsKey("--version")) return Say(opts, 0, AppInfo.Name + " tray " + AppInfo.Version + " on " + Environment.OSVersion.VersionString + ", .NET Framework " + Environment.Version);
            if (opts.ContainsKey("--selftest")) return SelfTest(opts);
            // What the self-update's signature check reads off a file (spec 2026-10-12): for the UAT and for support.
            if (opts.TryGetValue("--signer", out string? signedFile) && signedFile.Length > 0)
            {
                SignerInfo? s = new Authenticode().SignerOf(signedFile);
                return Say(opts, s == null ? 1 : 0, s == null ? "no valid signature" : "signer: " + s.SimpleName + " | subject: " + s.Subject + " | file version: " + s.FileVersion
                    + " | as an update of this build: " + SelfUpdate.CheckSigner(s, s, AppInfo.Version));
            }
            if (opts.ContainsKey("--once")) return Once(opts);
            if (args.Length > 0 && !opts.ContainsKey("--after-update")) return Say(opts, 1, "usage: Crucible.exe [--version | --selftest | --signer <file> | --once --config <file> --wow <folder> | --after-update <version>] [--out <file>]");
            return RunTray(opts);
        }

        private static int RunTray(Dictionary<string, string> opts)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (!SystemInformation.UserInteractive) return 1; // no desktop, no icon: it never runs unseen

            // Spec 2026-10-12: started by the copy it replaces, which is on its way out and still holds the lock.
            string? updatedFrom = opts.TryGetValue("--after-update", out string? from) && UpdateNudge.IsVersion(from) ? from : null;
            using (var single = new Mutex(false, @"Local\Crucible.Tray"))
            {
                bool first;
                try { first = single.WaitOne(updatedFrom != null ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
                catch (AbandonedMutexException) { first = true; }
                if (!first)
                {
                    MessageBox.Show("Crucible is already running - look for its icon next to the clock.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                var protector = new Dpapi();
                var log = new TrayLog(Paths.Log);

                // The rename (2026-10-04, remove at 1.0.0): Tallybook, this program's old name, must not run beside it -
                // it would keep putting its own addon back where this one retires it. Its Start with Windows entry goes
                // first, so the next sign-in starts only Crucible, whatever the person does about the one running now.
                try { Autostart.ForgetOldName(); }
                catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException || e is IOException)
                {
                    log.Write("could not remove Tallybook's Start with Windows entry: " + e.GetType().Name);
                }
                // Held for as long as this runs, so an old Tallybook.exe started later finds it and stops by itself.
                using Mutex? oldName = HoldOldName();
                if (oldName == null)
                {
                    MessageBox.Show("Tallybook - the old name of this program - is still running. Right-click its icon next to the clock, "
                        + "choose Quit, then start Crucible again.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                TrayConfig? config = LoadConfig(protector, log);
                if (config == null)
                {
                    // Not beside the exe (a browser may have saved it elsewhere, or renamed it "... (1).json"): the
                    // person points at it. Nothing is searched for.
                    MessageBox.Show("Crucible needs your settings file, crucible.config.json - the second download on the website.\n\n"
                        + "Press OK and pick it.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    config = SettingsFile.Pick(null, log);
                    if (config == null) return 1;
                    ConfigStore.Save(Paths.Config, config, protector);
                }

                // A newer notice means the program does something they have not agreed to yet: ask again.
                if (config.AcceptedNoticeVersion < Notice.VersionOf(SetupForm.NoticeText()) || !GameFolders.LooksLikeWow(config.WowFolder))
                {
                    using (var setup = new SetupForm(config, true))
                    {
                        if (setup.ShowDialog() != DialogResult.OK) return 0; // nothing starts without "I understand"
                        setup.ApplyTo(config);
                    }
                    ConfigStore.Save(Paths.Config, config, protector);
                }

                try { Autostart.Apply(config.StartWithWindows); }
                catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException || e is IOException)
                {
                    log.Write("could not set Start with Windows: " + e.GetType().Name);
                }

                try
                {
                    using (var app = new TrayApp(config, protector, log, updatedFrom)) Application.Run(app);
                }
                catch (InvalidOperationException e)
                {
                    log.Write("stopped: " + e.Message);
                    return 1;
                }
                return 0;
            }
        }

        /// <summary>
        /// Takes the one-per-session lock Tallybook 0.4.0 and before held while running, or null when that program holds
        /// it now. While Crucible holds it, an old Tallybook.exe says "already running" and exits. Remove at 1.0.0.
        /// </summary>
        private static Mutex? HoldOldName()
        {
            try
            {
                var held = new Mutex(true, @"Local\Tallybook.Tray", out bool notRunning);
                if (notRunning) return held;
                held.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException) { return null; } // there, just not ours to open
        }

        /// <summary>
        /// The saved config, or the download beside the exe. A NEW download wins for the credentials of THIS PC
        /// (it replaces none on the server: each settings file has its own key, and other PCs keep theirs) and keeps the
        /// folder and the switches; the plain file is then deleted.
        /// </summary>
        private static TrayConfig? LoadConfig(ISecretProtector protector, TrayLog log)
        {
            TrayConfig? saved = ConfigStore.Load(Paths.Config, protector);
            if (saved != null && ConfigStore.ReplaceOldHosts(saved, true))
            {
                // 0.99.1 carried the Tallybook config across with its old addresses: exactly those two, replaced once.
                // Done before a settings file beside the exe is looked at - that file is the member's and is never rewritten.
                try { ConfigStore.Save(Paths.Config, saved, protector); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log.Write("could not save the new addresses: " + e.GetType().Name);
                }
                log.Write("settings: the old addresses were replaced by the new ones");
            }
            if (saved == null)
            {
                // The rename (2026-10-04, remove at 1.0.0): a PC that ran Tallybook keeps its folder, switches and
                // credentials. A settings file beside the exe still wins for the credentials and addresses, below.
                try
                {
                    saved = ConfigStore.MoveFromOldName(Paths.OldConfig, Dpapi.OldName(), Paths.Config, protector);
                    if (saved != null && (new Uri(saved.Api).Host == "crucible-api.tigerforge.io" || new Uri(saved.Ui).Host == "crucible.tigerforge.io"))
                        log.Write("settings: the old addresses were replaced by the new ones");
                    if (saved != null) log.Write("took the settings Tallybook, this program's old name, had saved; its file is left where it was");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log.Write("could not move the Tallybook settings across: " + e.GetType().Name);
                }
            }
            TrayConfig? download = null;
            try
            {
                if (File.Exists(Paths.Download)) download = ConfigStore.ImportDownload(File.ReadAllText(Paths.Download, Encoding.UTF8));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log.Write("could not read the download's config: " + e.GetType().Name);
            }
            if (download == null) return saved;

            if (saved != null)
            {
                ConfigStore.ApplyCredentials(saved, download);
                download = saved;
            }
            try
            {
                ConfigStore.Save(Paths.Config, download, protector);
                File.Delete(Paths.Download); // the credentials now live sealed under %APPDATA%; the plain copy goes
                log.Write("took the credentials from the settings file and removed the plain file");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log.Write("could not move the download's config: " + e.GetType().Name);
            }
            return download;
        }

        private static int Once(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("--config", out string? configFile) || !opts.TryGetValue("--wow", out string? wow) || configFile.Length == 0 || wow.Length == 0)
                return Say(opts, 1, "--once needs --config <crucible.config.json> and --wow <folder>");
            TrayConfig? config = File.Exists(configFile) ? ConfigStore.ImportDownload(File.ReadAllText(configFile, Encoding.UTF8)) : null;
            if (config == null) return Say(opts, 1, "that is not a crucible.config.json");
            config.WowFolder = wow;
            config.AcceptedNoticeVersion = Notice.VersionOf(SetupForm.NoticeText());

            string work = Path.Combine(Path.GetTempPath(), "crucible-once-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var log = new TrayLog(Path.Combine(work, "log.txt"));
                using (var client = new ServerClient(config, ServerClient.CreateHandler()))
                {
                    var cycle = new Cycle(config, client, new SentLog(Path.Combine(work, "sent.txt")), new Stability(), log, () => DateTime.UtcNow);
                    cycle.RunAsync(false).GetAwaiter().GetResult();          // the first look
                    Thread.Sleep(Stability.Quiet + TimeSpan.FromMilliseconds(500));
                    CycleReport r = cycle.RunAsync(true).GetAwaiter().GetResult(); // quiet now: send, then fetch
                    string line = "uploaded=" + r.Uploaded + " rejected=" + r.Rejected + " wrote=" + r.Wrote + " state=" + r.State
                        + (client.LastError.Length > 0 ? " (" + client.LastError + ")" : "");
                    return Say(opts, r.State == TrayState.Ok ? 0 : r.State == TrayState.NeedsAttention ? 2 : 3, line);
                }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static int SelfTest(Dictionary<string, string> opts)
        {
            var lines = new List<string>();
            bool ok = true;
            void Check(string name, Func<bool> test)
            {
                bool passed;
                try { passed = test(); }
                catch (Exception e) { passed = false; name += " (" + e.GetType().Name + ")"; }
                lines.Add((passed ? "ok   " : "FAIL ") + name);
                ok &= passed;
            }

            var dpapi = new Dpapi();
            Check("DPAPI seals and opens", () => dpapi.Unprotect(dpapi.Protect("a secret")) == "a secret" && !dpapi.Protect("a secret").Contains("a secret"));
            Check("DPAPI refuses what was altered", () =>
            {
                // The sealed body and its check value, not the blob's descriptive header (which Windows does not check).
                string good = dpapi.Protect("a secret");
                foreach (int at in new[] { good.Length / 2, good.Length * 3 / 4, good.Length - 6 })
                {
                    char[] c = good.ToCharArray();
                    c[at] = c[at] == 'A' ? 'B' : 'A';
                    try { dpapi.Unprotect(new string(c)); return false; }
                    catch (System.Security.Cryptography.CryptographicException) { }
                    catch (FormatException) { }
                }
                return true;
            });
            Check("Tallybook's sealed config moves across to Crucible and is sealed again under the new name", () =>
            {
                // The rename (2026-10-04, remove at 1.0.0): the only place DPAPI with the old name's entropy is proven.
                string dir = Path.Combine(Path.GetTempPath(), "crucible-selftest-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string oldFile = Path.Combine(dir, "Tallybook", "config.json");
                    string newFile = Path.Combine(dir, "Crucible", "config.json");
                    Dpapi oldSeal = Dpapi.OldName();
                    ConfigStore.Save(oldFile, ConfigStore.ImportDownload("{\"api\":\"https://a.example.com\",\"ui\":\"https://b.example.com\",\"clientId\":\"id\",\"clientSecret\":\"selftest-secret\",\"uploadKey\":\"selftest-key\"}")!, oldSeal);
                    TrayConfig? moved = ConfigStore.MoveFromOldName(oldFile, oldSeal, newFile, dpapi);
                    TrayConfig? back = ConfigStore.Load(newFile, dpapi);
                    return moved != null && back != null && back.ClientSecret == "selftest-secret" && back.UploadKey == "selftest-key"
                        && ConfigStore.Load(oldFile, dpapi) == null // the new name's seal does not open the old one's
                        && File.Exists(oldFile);                    // and the old file is left where it was
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            });
            Check("the config round-trips under DPAPI and holds no secret in the clear", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "crucible-selftest-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string file = Path.Combine(dir, "config.json");
                    TrayConfig c = ConfigStore.ImportDownload("{\"api\":\"https://a.example.com\",\"ui\":\"https://b.example.com\",\"clientId\":\"id\",\"clientSecret\":\"selftest-secret\",\"uploadKey\":\"selftest-key\"}")!;
                    ConfigStore.Save(file, c, dpapi);
                    string text = File.ReadAllText(file);
                    TrayConfig? back = ConfigStore.Load(file, dpapi);
                    return back != null && back.ClientSecret == "selftest-secret" && back.UploadKey == "selftest-key" && !text.Contains("selftest-secret") && !text.Contains("selftest-key");
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            });
            Check("the HTTP client loads and follows no redirects", () =>
            {
                using (var handler = ServerClient.CreateHandler())
                using (new ServerClient(new TrayConfig { Api = "https://a.example.com" }, handler)) return !handler.AllowAutoRedirect;
            });
            Check("gzip", () => Payload.Gzip(Encoding.UTF8.GetBytes(new string('x', 10000))).Length < 200);
            Check("the four tray icons draw, with and without the update badge", () =>
            {
                foreach (TrayState s in new[] { TrayState.Ok, TrayState.Retrying, TrayState.NeedsAttention, TrayState.Paused })
                    if (Icons.For(s).Width <= 0 || Icons.For(s, true).Width <= 0) return false;
                return true;
            });
            Check("the risk notice is inside, and carries a version", () =>
                SetupForm.NoticeText().Contains("THE RISK")
                && SetupForm.NoticeText().Contains("not made, approved or supported by Blizzard")
                && Notice.VersionOf(SetupForm.NoticeText()) > 0);

            Check("this build's own signature is read: a signed build names its publisher, an unsigned one names nobody", () =>
            {
                SignerInfo? signer = new Authenticode().SignerOf(Paths.Exe);
                // CI runs this on the signed exe and expects the publisher; a local build is unsigned and expects null.
                bool signed = Environment.GetEnvironmentVariable("CRUCIBLE_SELFTEST_SIGNED") == "1";
                lines.Add("     signer: " + (signer == null ? "(none)" : signer.SimpleName + " · " + signer.Subject + " · file version " + signer.FileVersion));
                return signed ? signer != null && signer.SimpleName == AppInfo.Publisher && SelfUpdate.CheckSigner(signer, signer, AppInfo.Version) == Refusal.Ok : signer == null;
            });
            Check("the swap's renames work on a copy of this exe while that copy is running", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "crucible-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    UpdatePaths p = SelfUpdate.Plan(Path.Combine(dir, "Crucible.exe"));
                    File.Copy(Paths.Exe, p.Exe);
                    File.Copy(Paths.Exe, p.Staged);
                    using (Process running = Process.Start(new ProcessStartInfo(p.Exe, "--version --out \"" + Path.Combine(dir, "v.txt") + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                    {
                        // A running exe can be renamed on NTFS, and the staged one moved under its name.
                        File.Move(p.Exe, p.Old);
                        File.Move(p.Staged, p.Exe);
                        running.WaitForExit(30000);
                    }
                    return File.Exists(p.Exe) && File.Exists(p.Old) && !File.Exists(p.Staged);
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            });

            lines.Add(ok ? "selftest ok" : "selftest FAILED");
            return Say(opts, ok ? 0 : 1, string.Join(Environment.NewLine, lines));
        }

        private static Dictionary<string, string> Options(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                opts[args[i]] = hasValue ? args[++i] : "";
            }
            return opts;
        }

        private static int Say(Dictionary<string, string> opts, int exitCode, string text)
        {
            Console.Out.WriteLine(text);
            if (opts.TryGetValue("--out", out string? file) && file.Length > 0)
            {
                try { File.WriteAllText(file, text + Environment.NewLine); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return exitCode == 0 ? 1 : exitCode; }
            }
            return exitCode;
        }
    }
}
