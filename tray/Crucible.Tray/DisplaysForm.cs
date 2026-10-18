using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Crucible.Tray
{
    /// <summary>
    /// Spec 2026-10-15 §6.5: the Displays window. Tick two displays (or one in split mode), choose the Mainhand, Identify,
    /// auto-span (off by default) with its delay, Span WoW now, Restore window, and the live status line. Reached from
    /// Settings and from the tray menu. Nothing moves until Span is pressed; a changed choice is saved on OK.
    /// Ruling P2-R18: with Canvas ticked, OK and Span WoW now first run the plan on the choice as edited; a refusal shows
    /// its reason (red) and nothing is saved or moved. With Canvas off, OK saves without validating (nothing will span).
    /// Displays are shown by name, size and "(primary)"; their ids never appear on screen. The status words are shown,
    /// never logged (ruling P2-R15).
    /// </summary>
    internal sealed class DisplaysForm : Form
    {
        private const string PickTwo = "Tick the two displays WoW should span:";
        private const string PickOne = "Tick the one display to split in half:";

        private readonly CheckBox enable = new CheckBox { AutoSize = true, Text = SetupWords.CanvasEnable };
        private readonly Label sentence = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Text = SetupWords.CanvasSwitch, ForeColor = SystemColors.GrayText };
        private readonly Label pick = new Label { AutoSize = true, Text = PickTwo };
        private readonly CheckedListBox list = new CheckedListBox { Size = new Size(560, 110), CheckOnClick = true };
        private readonly ComboBox mainhand = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly CheckBox split = new CheckBox { AutoSize = true, Text = SetupWords.CanvasSplit };
        private readonly RadioButton splitLeft = new RadioButton { AutoSize = true, Text = "game on the left", Checked = true };
        private readonly RadioButton splitRight = new RadioButton { AutoSize = true, Text = "game on the right" };
        private readonly CheckBox auto = new CheckBox { AutoSize = true, Text = SetupWords.CanvasAuto };
        private readonly NumericUpDown delay = new NumericUpDown { Minimum = 0, Maximum = 60, Width = 60 };
        private readonly Label statusLabel = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };
        private readonly Button identify = new Button { Text = "Identify", Width = 90 };
        private readonly Button span = new Button { Text = "Span WoW now", Width = 130 };
        private readonly Button restore = new Button { Text = "Restore window", Width = 130 };
        private readonly Button ok = new Button { Text = "OK", Width = 90 };   // no DialogResult: OK validates first (P2-R18)
        private readonly IWindowOps ops;
        private readonly IReadOnlyList<DisplayInfo> displays;   // the list's rows, in order
        private readonly Func<SpanStatus> statusOf;
        private readonly Timer refresh = new Timer { Interval = 2000 };
        private readonly Action<string>? log;
        private bool held;   // the status line shows a refusal (or a failure): the refresh leaves it until the next edit

        /// <summary>Ruling S-R10: Span WoW now applied the edited choice to the live config. The tray saves it even when
        /// the window then closes by Cancel, so the saved settings say what the window is spanned to.</summary>
        public bool ChoiceApplied { get; private set; }

        /// <summary>Something went wrong in one of the window's handlers; the log gets the exception's type name.</summary>
        public const string Failed = "Something went wrong - see the log";

        public DisplaysForm(TrayConfig config, IWindowOps ops, Func<SpanStatus> status, Func<SpanStatus> spanNow, Func<SpanStatus> restoreNow, Action<string>? log = null)
        {
            Text = SetupWords.DisplaysTitle;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            ClientSize = new Size(584, 470);
            Font = SystemFonts.MessageBoxFont;
            this.ops = ops;
            displays = ops.Displays();
            statusOf = status;
            this.log = log;

            int y = 12;
            enable.Location = new Point(12, y); enable.Checked = config.CanvasEnabled; y += 26;
            sentence.Location = new Point(12, y); y += 44;
            pick.Location = new Point(12, y); y += 20;
            list.Location = new Point(12, y);
            int row = 1;   // the number Identify shows on that display
            foreach (DisplayInfo d in displays)
                list.Items.Add((row++) + ".  " + d.Name + "  " + d.Bounds.Width + "x" + d.Bounds.Height + (d.IsPrimary ? "  (primary)" : ""), config.CanvasDisplays.Contains(d.Id));
            y += 116;
            var mainLabel = new Label { AutoSize = true, Text = "Mainhand (game) display:", Location = new Point(12, y + 3) };
            mainhand.Location = new Point(190, y); y += 30;
            split.Location = new Point(12, y); split.Checked = config.CanvasMode == "SPLIT";
            splitLeft.Location = new Point(260, y); splitRight.Location = new Point(390, y); y += 28;
            splitLeft.Checked = config.CanvasSplitSide != "right"; splitRight.Checked = config.CanvasSplitSide == "right";
            auto.Location = new Point(12, y); auto.Checked = config.CanvasAutoSpan;
            delay.Location = new Point(330, y - 2); delay.Value = Math.Max(0, Math.Min(60, config.CanvasSpanDelaySeconds));
            var seconds = new Label { AutoSize = true, Text = "seconds", Location = new Point(396, y + 2) }; y += 34;
            identify.Location = new Point(12, y); span.Location = new Point(110, y); restore.Location = new Point(248, y); y += 36;
            statusLabel.Location = new Point(12, y);
            ok.Location = new Point(386, 430);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Location = new Point(482, 430) };
            AcceptButton = ok; CancelButton = cancel;

            // Every handler is guarded: an exception escaping one would end the tray (and the refresh would raise it every 2 s).
            list.ItemCheck += (s, e) => Guard(() => { Edited(); BeginInvoke(new Action(() => Guard(FillMainhand))); });   // ItemCheck fires before the tick lands
            split.CheckedChanged += (s, e) => Guard(() => { Edited(); FillMainhand(); });
            mainhand.SelectedIndexChanged += (s, e) => Guard(Edited);
            splitLeft.CheckedChanged += (s, e) => Guard(Edited);
            enable.CheckedChanged += (s, e) => Guard(Edited);
            auto.CheckedChanged += (s, e) => Guard(() => delay.Enabled = auto.Checked);
            identify.Click += (s, e) => Guard(() => Identify(displays, log));
            span.Click += (s, e) => Guard(() =>
            {
                // Validate before touching the live config: a refused choice is not applied, and Canvas unticked here
                // says so rather than switching Canvas off under a spanned window.
                if (!enable.Checked) { ShowRefusal(SpanController.TurnOnFirst); return; }
                string? why = Refusal();
                if (why != null) { ShowRefusal(why); return; }
                ApplyTo(config);
                ChoiceApplied = true;
                held = false;
                ShowStatus(spanNow());
            });
            restore.Click += (s, e) => Guard(() => { held = false; ShowStatus(restoreNow()); });
            ok.Click += (s, e) => Guard(() =>
            {
                string? why = Refusal();
                if (why != null) { ShowRefusal(why); return; }   // not closed, not saved; Cancel stays available
                DialogResult = DialogResult.OK;
            });
            refresh.Tick += (s, e) => Guard(() => { if (!held) ShowStatus(statusOf()); });
            refresh.Start();
            Controls.AddRange(new Control[] { enable, sentence, pick, list, mainLabel, mainhand, split, splitLeft, splitRight, auto, delay, seconds, identify, span, restore, statusLabel, ok, cancel });
            FillMainhand();
            if (config.CanvasMainhand.Length > 0)
                for (int i = 0; i < mainhand.Items.Count; i++) if (((Choice)mainhand.Items[i]).Id == config.CanvasMainhand) mainhand.SelectedIndex = i;
            delay.Enabled = auto.Checked;
            ShowStatus(statusOf());
        }

        /// <summary>Runs a handler; an exception shows <see cref="Failed"/> (held, so the refresh does not repeat it every
        /// 2 s) and logs only its type name - never the status words or a display id (P2-R15).</summary>
        private void Guard(Action handler)
        {
            try { handler(); }
            catch (Exception e)
            {
                held = true;
                statusLabel.Text = Failed;
                statusLabel.ForeColor = Color.Firebrick;
                log?.Invoke("displays window: " + e.GetType().Name);
            }
        }

        private sealed class Choice { public string Id = ""; public string Text = ""; public override string ToString() => Text; }

        private void FillMainhand()
        {
            string? keep = (mainhand.SelectedItem as Choice)?.Id;
            mainhand.Items.Clear();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i)) mainhand.Items.Add(new Choice { Id = displays[i].Id, Text = displays[i].Name });
            for (int i = 0; i < mainhand.Items.Count; i++) if (((Choice)mainhand.Items[i]).Id == keep) mainhand.SelectedIndex = i;
            if (mainhand.SelectedIndex < 0 && mainhand.Items.Count > 0) mainhand.SelectedIndex = 0;
            mainhand.Enabled = !split.Checked; splitLeft.Enabled = split.Checked; splitRight.Enabled = split.Checked;
            pick.Text = split.Checked ? PickOne : PickTwo;
        }

        /// <summary>A change to the choice: a held refusal no longer describes it, so the live status comes back.</summary>
        private void Edited()
        {
            if (!held) return;
            held = false;
            ShowStatus(statusOf());
        }

        /// <summary>P2-R18: why the choice as edited cannot span (the plan's own words, on the displays as they are now);
        /// null when it can, or when Canvas is unticked (nothing will span, so nothing to validate).</summary>
        private string? Refusal()
        {
            if (!enable.Checked) return null;
            var edited = new TrayConfig();
            ApplyTo(edited);   // the same mapping OK saves, so the check cannot drift from what is saved
            PlanResult plan = DisplayPlan.Plan(ops.Displays(), edited.CanvasChoice());
            return plan.Ok ? null : plan.Reason;
        }

        private void ShowRefusal(string why)
        {
            held = true;
            statusLabel.Text = "Status: " + why;
            statusLabel.ForeColor = Color.Firebrick;
        }

        private void ShowStatus(SpanStatus s) { statusLabel.Text = "Status: " + s.Words; statusLabel.ForeColor = s.State == SpanState.Refused ? Color.Firebrick : SystemColors.ControlText; }

        /// <summary>Copies what the person chose into the config. Call after ShowDialog returned OK (Span WoW now has already
        /// applied it when <see cref="ChoiceApplied"/>).</summary>
        public void ApplyTo(TrayConfig config)
        {
            config.CanvasEnabled = enable.Checked;
            config.CanvasMode = split.Checked ? "SPLIT" : "DUAL";
            var ids = new List<string>();
            for (int i = 0; i < list.Items.Count; i++) if (list.GetItemChecked(i)) ids.Add(displays[i].Id);
            config.CanvasDisplays = ids;
            config.CanvasMainhand = split.Checked ? "" : ((mainhand.SelectedItem as Choice)?.Id ?? "");   // split has no Mainhand to choose
            config.CanvasSplitSide = splitRight.Checked ? "right" : "left";
            config.CanvasAutoSpan = auto.Checked;
            config.CanvasSpanDelaySeconds = (int)delay.Value;
        }

        /// <summary>A big number and the display's name on each display for three seconds, 40 px in from the display's own
        /// top-left corner, then gone. Brand colours, square corners. It never takes the focus from the Displays window.
        /// Ruling S-R9: the process is DPI-unaware but the display rects are physical, so each overlay is created, placed
        /// and shown inside a Per-Monitor V2 thread scope; the window keeps that context, so its coordinates stay physical.</summary>
        public static void Identify(IReadOnlyList<DisplayInfo> displays, Action<string>? log = null)
        {
            string family;
            using (Font system = SystemFonts.MessageBoxFont) family = system.FontFamily.Name;
            int n = 1;
            using (DpiScope.PerMonitorV2())
            {
                foreach (DisplayInfo d in displays)
                {
                    var font = new Font(family, 40, FontStyle.Bold);
                    string text = (n++).ToString() + "\n" + d.Name.Replace("\\\\.\\", "");
                    Size need = TextRenderer.MeasureText(text, font);   // sized to its words: a fixed box clips at 150 %
                    var f = new IdentifyWindow
                    {
                        FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                        BackColor = Color.FromArgb(20, 18, 17), ClientSize = new Size(need.Width + 48, need.Height + 32),
                        Location = new Point(d.Bounds.Left + 40, d.Bounds.Top + 40),
                    };
                    f.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(226, 176, 74), Font = font, Text = text });
                    var t = new Timer { Interval = 3000 };
                    t.Tick += (s, e) =>
                    {
                        t.Stop();
                        try { f.Close(); f.Dispose(); }
                        catch (Exception ex) { log?.Invoke("identify window: " + ex.GetType().Name); }
                        finally { font.Dispose(); t.Dispose(); }
                    };
                    f.Show(); t.Start();   // Show creates the window: inside the scope, so it is Per-Monitor V2
                }
            }
        }

        /// <summary>Topmost from creation and never activated, so the Displays window keeps the focus while it shows.</summary>
        private sealed class IdentifyWindow : Form
        {
            private const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { CreateParams p = base.CreateParams; p.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return p; }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { refresh.Stop(); base.OnFormClosed(e); }

        protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }
    }
}
