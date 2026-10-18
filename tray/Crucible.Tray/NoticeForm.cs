using System.Drawing;
using System.Windows.Forms;

namespace Crucible.Tray
{
    /// <summary>Spec 2026-10-15 §8 (owner 2026-10-16): the notice shown to read, with one Close button - nothing to tick,
    /// nothing blocked. Opened once for a version carried over by acceptance, and any time from "View notice".</summary>
    internal sealed class NoticeForm : Form
    {
        public NoticeForm(string noticeText, bool firstShowing)
        {
            Text = firstShowing ? "Crucible - the notice has changed" : "Crucible - notice";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            ClientSize = new Size(640, 520);
            Font = SystemFonts.MessageBoxFont;
            var text = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, TabStop = false, BackColor = SystemColors.Window,
                Text = noticeText.Replace("\r\n", "\n").Replace("\n", "\r\n"), Location = new Point(12, 12), Size = new Size(616, 456),
            };
            text.Select(0, 0);
            var hint = new Label { AutoSize = true, Location = new Point(12, 480), ForeColor = SystemColors.GrayText,
                Text = firstShowing ? "Nothing changes unless you turn Crucible Canvas on in Settings > Displays." : "" };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 90, Location = new Point(538, 478) };
            AcceptButton = close; CancelButton = close;
            Controls.AddRange(new Control[] { text, hint, close });
        }
    }
}
