using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Crucible.Tray
{
    /// <summary>
    /// First run and Settings are the same window: the notice, the folder the person picks (never detected, never
    /// guessed - C4), and the two switches. On first run nothing starts until "I understand" is ticked.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly TextBox folder = new TextBox { ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        // Both wrap within the window: the words describe the folder's shape, which takes more than one line (M113).
        private readonly Label folderHint = new Label { AutoSize = true, MaximumSize = new Size(616, 0), ForeColor = Color.Firebrick };
        private readonly CheckBox understand = new CheckBox { AutoSize = true, Text = "I have read this and I understand the risk" };
        private readonly CheckBox startWithWindows = new CheckBox { AutoSize = true, Text = "Start with Windows" };
        private readonly CheckBox bringDataBack = new CheckBox { AutoSize = true, Text = "Bring data back (write the shared Data.lua into the Crucible addon's folder)" };
        private readonly CheckBox keepAddon = new CheckBox { AutoSize = true, Text = "Keep the Crucible addon installed and up to date" };
        private readonly Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };

        /// <summary>Settings only: the credentials of a settings file the person loaded here; null when they did not.</summary>
        public TrayConfig? NewCredentials { get; private set; }

        public SetupForm(TrayConfig config, bool firstRun)
        {
            Text = firstRun ? "Crucible - before you start" : "Crucible - settings";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(640, 610);
            Font = SystemFonts.MessageBoxFont;

            var notice = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                Text = NoticeText().Replace("\r\n", "\n").Replace("\n", "\r\n"),
                Location = new Point(12, 12), Size = new Size(616, 300), TabStop = false, BackColor = SystemColors.Window,
            };
            notice.Select(0, 0);

            var folderLabel = new Label { AutoSize = true, MaximumSize = new Size(616, 0), Text = SetupWords.FolderLabel, Location = new Point(12, 322) };
            folder.Location = new Point(12, 360);
            folder.Size = new Size(516, 24);
            folder.Text = config.WowFolder;
            var browse = new Button { Text = "Browse...", Location = new Point(538, 358), Width = 90 };
            browse.Click += (s, e) => Browse();
            folderHint.Location = new Point(12, 388);

            understand.Location = new Point(12, 424);
            // Ticked only for this notice: an older "I understand" does not carry over to words they have not read.
            understand.Checked = Notice.IsAccepted(config.AcceptedNoticeVersion, Notice.VersionOf(NoticeText()));
            startWithWindows.Location = new Point(12, 456);
            startWithWindows.Checked = config.StartWithWindows;
            bringDataBack.Location = new Point(12, 484);
            bringDataBack.Checked = config.BringDataBack;
            keepAddon.Location = new Point(12, 512);
            keepAddon.Checked = config.KeepAddonUpToDate;

            if (!firstRun)
            {
                // A new download does not retire the key of an older one. A running copy is refused ("The server refused
                // this PC's credentials", red) only when the member revoked THIS PC on Your PCs, or the owner revoked the
                // member; this is how it is given a fresh settings file without hunting for the folder it lives in.
                var load = new Button { Text = "Load new settings file...", Location = new Point(12, 568), Width = 190 };
                var loaded = new Label { AutoSize = true, Location = new Point(210, 573), ForeColor = Color.SeaGreen };
                load.Click += (s, e) =>
                {
                    TrayConfig? fresh = SettingsFile.Pick(this, new TrayLog(Paths.Log));
                    if (fresh == null) return;
                    NewCredentials = fresh;
                    loaded.Text = "Loaded - press OK";
                };
                Controls.Add(load);
                Controls.Add(loaded);
            }

            ok.Location = new Point(442, 568);
            var cancel = new Button { Text = firstRun ? "Quit" : "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Location = new Point(538, 568) };
            AcceptButton = ok;
            CancelButton = cancel;

            understand.CheckedChanged += (s, e) => Check();
            Controls.AddRange(new Control[] { notice, folderLabel, folder, browse, folderHint, understand, startWithWindows, bringDataBack, keepAddon, ok, cancel });
            Check();
        }

        /// <summary>Copies what the person chose into the config. Call after ShowDialog returned OK.</summary>
        public void ApplyTo(TrayConfig config)
        {
            config.WowFolder = folder.Text;
            config.AcceptedNoticeVersion = understand.Checked ? Notice.VersionOf(NoticeText()) : 0;
            config.StartWithWindows = startWithWindows.Checked;
            config.BringDataBack = bringDataBack.Checked;
            config.KeepAddonUpToDate = keepAddon.Checked;
        }

        public static string NoticeText()
        {
            using (Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("risk-notice.txt"))
            {
                if (s == null) return "";
                using (var reader = new StreamReader(s)) return reader.ReadToEnd();
            }
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = SetupWords.BrowseDescription, ShowNewFolderButton = false })
            {
                if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            }
            Check();
        }

        private void Check()
        {
            bool looksRight = GameFolders.LooksLikeWow(folder.Text);
            folderHint.Text = folder.Text.Length == 0 ? SetupWords.FolderEmpty
                : looksRight ? ""
                : SetupWords.FolderWrong;
            ok.Enabled = looksRight && understand.Checked;
        }
    }
}
