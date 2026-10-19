using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Crucible.Tray
{
    /// <summary>
    /// Help > About Crucible (owner 2026-10-18, spec 2026-10-17 section 11): three tabs of read-only text - the versions, the
    /// release notes and the notice - and one Close button, like <see cref="NoticeForm"/>: nothing to tick, nothing blocked. It
    /// is always one menu away, so the notice is never out of reach. What the words are is <see cref="AboutText"/>'s (the
    /// core, unit-tested); this window only shows them.
    /// </summary>
    internal sealed class AboutForm : Form
    {
        public AboutForm(string versions, string releaseNotes, string notice)
        {
            Text = "About Crucible";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            ClientSize = new Size(720, 520);   // wider than the notice window: the changelog's lines are wrapped at 115 columns
            Font = SystemFonts.MessageBoxFont;
            var tabs = new TabControl { Location = new Point(12, 12), Size = new Size(696, 456) };
            tabs.TabPages.AddRange(new[] { Page("About", versions), Page("Release notes", releaseNotes), Page("Notice", notice) });
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 90, Location = new Point(618, 478) };
            AcceptButton = close; CancelButton = close;
            Controls.AddRange(new Control[] { tabs, close });
        }

        /// <summary>One tab: the text, read-only, filling the page - the look of <see cref="NoticeForm"/>'s text.</summary>
        private static TabPage Page(string title, string text)
        {
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, TabStop = false, BackColor = SystemColors.Window,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Dock = DockStyle.Fill,
            };
            box.Select(0, 0);
            var page = new TabPage(title);
            page.Controls.Add(box);
            return page;
        }

        /// <summary>shared/CHANGELOG.txt as it was embedded when this exe was built; "" when the build holds none.</summary>
        public static string ChangelogText()
        {
            using (Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.txt"))
            {
                if (s == null) return "";
                using (var reader = new StreamReader(s)) return reader.ReadToEnd();
            }
        }
    }
}
