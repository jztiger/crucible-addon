using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Crucible.Tray
{
    public sealed class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string ExePath { get; set; } = "";
        public string Title { get; set; } = "";
        public Rect Bounds { get; set; }
        public bool HasCaption { get; set; }
        public bool Minimized { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>The seam between the core and Win32 (spec §6.4). Exactly what D3 permits, nothing else.</summary>
    public interface IWindowOps
    {
        IReadOnlyList<DisplayInfo> Displays();
        /// <summary>The visible, titled top-level windows whose program <paramref name="isWowProgram"/> accepts (it is handed the
        /// exe path). Ruling S-R19: of any other window only its visibility, its process and its program path are read - never
        /// its title, bounds, style or state. A rule that throws counts as "not WoW".</summary>
        IReadOnlyList<WindowInfo> TopLevelWindows(Func<string, bool> isWowProgram);
        bool StripBorder(IntPtr handle, out int error);
        bool RestoreBorder(IntPtr handle, out int error);
        bool Move(IntPtr handle, Rect to, out int error);
        Rect? BoundsOf(IntPtr handle);
        void Focus(IntPtr handle);
    }

    public enum SpanState { Idle, Found, Spanned, Restored, Refused }

    public sealed class SpanStatus
    {
        public SpanState State { get; set; }
        public string Words { get; set; } = "";
        public Rect? Window { get; set; }
        public PlanResult? Plan { get; set; }
    }

    /// <summary>Spec §6.3: Idle → Found → Spanned → Restored, with Refused(reason). Never spans without a click unless auto-span
    /// is on; one auto-span per window; a restored window waits for a click or a new process; a missing display restores and
    /// refuses; a spanned window that moved, or whose plan changed, steps down to Found (ruling P2-R9); a window already at the
    /// plan's box with no caption is adopted as spanned (ruling P2-R10). The shell calls Tick every two seconds and the two
    /// buttons, on the UI thread only.</summary>
    public sealed class SpanController
    {
        /// <summary>Ruling P2-R15: every status and refusal is at most this long, so <see cref="TrayText.MaxTooltip"/> (63) shows it whole.</summary>
        public const int MaxWords = 60;

        // The words (ruling P2-R15): roles, never ids or paths; each at most MaxWords.
        public const string NotRunning = "WoW not running";
        public const string CanvasOff = "Crucible Canvas is off";
        public const string NoDisplay = "no display reported - nothing restored";
        public const string CanvasOffNoDisplay = "Canvas is off - no display reported - nothing restored";
        public const string FullscreenWords = "WoW is Windowed (Fullscreen) - set Display Mode to Windowed";
        public const string Found = "WoW found";
        public const string FoundWindowed = "WoW found - windowed";
        public const string FoundPressSpan = "WoW found - press Span WoW now";
        public const string FoundWindowedPressSpan = "WoW found - windowed; press Span WoW now";
        public const string DisplaysChanged = "the displays changed - press Span WoW now";
        public const string WowMoved = "WoW moved its window - press Span WoW now";
        public const string TurnOnFirst = "turn on Crucible Canvas first";
        public const string Minimised = "bring WoW back up first";
        /// <summary>UAT row 44 (owner: refuse): Windows keeps WS_MAXIMIZE under a borderless span and WoW keeps drawing its old size.</summary>
        public const string Maximised = "WoW's window is maximised - restore it from its title bar";
        public const string Privilege = "error 5: run WoW and Crucible at the same privilege level";
        public const string NothingToRestore = "nothing to restore";
        public const string RestoredWords = "Window restored - press Span WoW now to span again";

        public static string SpannedWords(Rect window, bool adopted) => "Spanned " + window.Width + "x" + window.Height + (adopted ? " (adopted)" : "");
        public static string CouldNotChange(int err) => "could not change the window (error " + err + ")";
        public static string MoveFailed(int err) => "the move failed (error " + err + ")";
        public static string MoveDidNotTake(string kept) => "move did not take - WoW kept " + kept;

        private readonly IWindowOps ops;
        private readonly Func<DateTime> nowUtc;
        private readonly TrayLog log;
        private IntPtr window = IntPtr.Zero;
        private DateTime seenUtc;
        private bool autoDone;      // this window was auto-spanned (or restored by hand): no second auto-span
        private Rect? before;       // bounds before our first span of this window; never a minimised window's
        private bool spanned;       // the window matches the plan it was spanned (or adopted) to
        private bool stripped;      // we took this window's border off (or adopted it borderless) and have not put it back:
                                    // still true after a step-down, so Canvas off and a vanished display still restore it
        private PlanResult? spannedPlan;   // the plan the window was spanned (or adopted) to: a different one steps down
        private bool recheck;       // the current refusal came from the plan, the fullscreen, the minimised or the maximised check: Tick looks again
        private Func<string, bool> foundWith = _ => false;   // the program rule the window was found with: RestoreNow looks with it too
        public SpanStatus Status { get; private set; } = new SpanStatus { State = SpanState.Idle, Words = NotRunning };
        public event Action<PlanResult, IntPtr>? Spanned;

        public SpanController(IWindowOps ops, Func<DateTime> nowUtc, TrayLog log) { this.ops = ops; this.nowUtc = nowUtc; this.log = log; }

        /// <summary>C4: a window is WoW's when its program passes <see cref="IsWowProgram"/>, and it has a title. Ruling S-R19: the
        /// program is looked at first - the shell reads the title and the rest only of a window whose program passes.</summary>
        public static bool IsWowWindow(WindowInfo w, string wowFolder, IReadOnlyList<string> products) =>
            !string.IsNullOrEmpty(w.Title) && IsWowProgram(w.ExePath, wowFolder, products);

        /// <summary>C4's program rule: the exe sits DIRECTLY in &lt;wowFolder&gt;\&lt;product&gt;\ for a listed product. The shell
        /// calls it inside the native window enumeration, so it never throws.</summary>
        public static bool IsWowProgram(string exePath, string wowFolder, IReadOnlyList<string> products)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(wowFolder)) return false;
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (dir == null) return false;
                foreach (string product in products)
                {
                    if (!ForeverProducts.IsProductName(product)) continue;
                    string expected = Path.GetFullPath(Path.Combine(wowFolder, product)).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception) { return false; }   // a folder or exe path that is not a path is not WoW
            return false;
        }

        /// <summary>The largest WoW window, or null. The shell is handed C4's program rule (ruling S-R19), so of any other window it
        /// reads only the program; <see cref="IsWowWindow"/> still decides each window it returns. The rule is kept for
        /// <see cref="RestoreNow"/>.</summary>
        private WindowInfo? Find(TrayConfig config)
        {
            string folder = config.WowFolder;
            IReadOnlyList<string> products = config.Products;
            Func<string, bool> rule = exe => IsWowProgram(exe, folder, products);
            WindowInfo? best = null;
            foreach (WindowInfo w in ops.TopLevelWindows(rule))
            {
                if (!IsWowWindow(w, folder, products)) continue;
                if (best == null || (long)w.Bounds.Width * w.Bounds.Height > (long)best.Bounds.Width * best.Bounds.Height) best = w;
            }
            if (best != null) foundWith = rule;
            return best;
        }

        private static readonly Rect DefaultBounds = new Rect(0, 0, 1920, 1080);

        private SpanStatus Set(SpanState state, string words, PlanResult? plan = null, Rect? window = null)
        {
            recheck = false;
            Status = new SpanStatus { State = state, Words = words, Plan = plan, Window = window };
            return Status;
        }

        /// <summary>A refusal whose cause (the plan, Windowed (Fullscreen) mode, a minimised or maximised window) Tick looks at again, so the words follow the fix.</summary>
        private SpanStatus Blocked(string why)
        {
            Set(SpanState.Refused, why);
            recheck = true;
            return Status;
        }

        /// <summary>Why this window cannot be spanned right now (minimised, maximised, the plan refuses, or it is
        /// fullscreen-windowed on a display that is not the plan's window); null when it can.</summary>
        private string? Blocker(TrayConfig config, WindowInfo w, out PlanResult plan)
        {
            plan = DisplayPlan.Plan(ops.Displays(), config.CanvasChoice());
            if (w.Minimized) return Minimised;
            if (w.Maximized) return Maximised;
            if (!plan.Ok) return plan.Reason;
            Rect box = plan.Window;
            if (!w.HasCaption && !stripped && ops.Displays().Any(d => d.Bounds == w.Bounds && d.Bounds != box)) return FullscreenWords;
            return null;
        }

        private static bool SamePlan(PlanResult a, PlanResult b) =>
            a.Window == b.Window && a.Mainhand == b.Mainhand && a.Workspace == b.Workspace
            && a.MainhandId == b.MainhandId && a.WorkspaceId == b.WorkspaceId && a.Mode == b.Mode;

        /// <summary>A new window handle: forget the last one. A caption-less window already at the plan's box is adopted
        /// as spanned (a tray restart or self-update while spanned, or SPLIT in Windowed (Fullscreen) on the chosen display).</summary>
        private SpanStatus? Meet(TrayConfig config, WindowInfo w)
        {
            window = w.Handle; seenUtc = nowUtc(); autoDone = false; spanned = false; stripped = false; before = null; spannedPlan = null;
            if (Status.State == SpanState.Restored) Set(SpanState.Found, Found);   // a restore was for the old window
            PlanResult plan = DisplayPlan.Plan(ops.Displays(), config.CanvasChoice());
            if (!w.Minimized && !w.HasCaption && plan.Ok && w.Bounds == plan.Window)
            {
                spanned = true; stripped = true; autoDone = true; spannedPlan = plan;
                log.Write("WoW window found, already spanned " + plan.Window.Width + "x" + plan.Window.Height + " (" + plan.Mode + ") - adopted");
                Set(SpanState.Spanned, SpannedWords(plan.Window, true), plan, plan.Window);
                Raise(plan, w.Handle);   // so the shell writes Topology.lua, which may never have been written for this window
                return Status;
            }
            log.Write("WoW window found");
            return null;
        }

        public SpanStatus Tick(TrayConfig config)
        {
            if (!config.CanvasEnabled)
            {
                if (stripped)
                {
                    // Ruling P2-R3: switching Canvas off puts the window back (also one that stepped down: it is still borderless). The window counts as new when it is switched on again.
                    Rect? to = Fit(before ?? DefaultBounds, ops.Displays());
                    if (to == null) return Set(SpanState.Idle, CanvasOffNoDisplay);
                    RestoreTo(to.Value);
                    log.Write("canvas off - window restored");
                    window = IntPtr.Zero; before = null; autoDone = false;
                }
                return Set(SpanState.Idle, CanvasOff);
            }
            WindowInfo? w = Find(config);
            if (w == null)
            {
                window = IntPtr.Zero; spanned = false; stripped = false; autoDone = false; before = null; spannedPlan = null;
                return Set(SpanState.Idle, NotRunning);
            }
            if (w.Handle != window)
            {
                SpanStatus? adopted = Meet(config, w);
                if (adopted != null) return adopted;
            }
            if (stripped)
            {
                // Still ours? A vanished display restores to the survivor and refuses until it is back - also after a
                // step-down, since the window is still borderless across the old box.
                PlanResult check = DisplayPlan.Plan(ops.Displays(), config.CanvasChoice());
                if (!check.Ok)
                {
                    autoDone = true;
                    Rect? to = SurvivingWorkArea(config);
                    if (to == null) return Set(SpanState.Refused, NoDisplay);   // still borderless: nothing to fit it into
                    log.Write("spanned, but a chosen display is gone - restoring");
                    RestoreTo(to.Value);
                    return Blocked(check.Reason);
                }
                if (spanned)
                {
                    // Ruling P2-R9: the plan first (a swapped Mainhand or side keeps the same box), then the bounds. A minimised
                    // window's bounds are where Windows parked it, so they say nothing. Stepped down, the window keeps no
                    // border until Span or Restore - nothing moves by itself.
                    if (spannedPlan != null && !SamePlan(check, spannedPlan))
                    {
                        spanned = false;
                        log.Write("spanned, but the displays changed - waiting for Span WoW now");
                        return Set(SpanState.Found, DisplaysChanged);
                    }
                    if (!w.Minimized && ops.BoundsOf(window) != check.Window)
                    {
                        spanned = false;
                        log.Write("spanned, but WoW moved its window - waiting for Span WoW now");
                        return Set(SpanState.Found, WowMoved);
                    }
                    if (Status.State != SpanState.Spanned)
                        return Set(SpanState.Spanned, SpannedWords(check.Window, false), check, check.Window);   // the displays came back with nothing moved
                    return Status;
                }
            }
            if (Status.State == SpanState.Restored) return Status;
            if (Status.State == SpanState.Refused && autoDone)
            {
                // Look again: the words follow the fix. No retry by itself - the one attempt for this window is spent.
                string? why = Blocker(config, w, out _);
                if (why != null) return Blocked(why);
                return recheck ? Set(SpanState.Found, FoundWindowedPressSpan) : Status;
            }
            // Auto-span waits while the window is minimised or maximised: the one attempt is not spent on a refusal.
            if (config.CanvasAutoSpan && !autoDone && !w.Minimized && !w.Maximized && (nowUtc() - seenUtc).TotalSeconds >= Math.Max(0, Math.Min(60, config.CanvasSpanDelaySeconds)))
            {
                autoDone = true;
                return Span(config, w);
            }
            // Once the attempt is spent (or the window stepped down), the words say what to do next.
            return Set(SpanState.Found, autoDone ? FoundPressSpan : w.HasCaption ? FoundWindowed : Found);
        }

        public SpanStatus SpanNow(TrayConfig config)
        {
            if (!config.CanvasEnabled) return Set(SpanState.Refused, TurnOnFirst);
            WindowInfo? w = Find(config);
            if (w == null) { window = IntPtr.Zero; return Set(SpanState.Refused, NotRunning); }
            if (w.Handle != window) { window = w.Handle; seenUtc = nowUtc(); before = null; spanned = false; stripped = false; spannedPlan = null; }
            autoDone = true;
            return Span(config, w);
        }

        private SpanStatus Span(TrayConfig config, WindowInfo w)
        {
            string? why = Blocker(config, w, out PlanResult plan);
            if (why != null) return Blocked(why);
            if (before == null) before = w.Bounds;   // never a minimised or maximised window's: Blocker refused those above
            if (!ops.StripBorder(w.Handle, out int err))
                return Set(SpanState.Refused, err == 5 ? Privilege : CouldNotChange(err));
            if (!ops.Move(w.Handle, plan.Window, out err) || ops.BoundsOf(w.Handle) != plan.Window)
            {
                string kept = ops.BoundsOf(w.Handle)?.ToString() ?? "no window";   // what the move left, before the undo moves it
                ops.RestoreBorder(w.Handle, out _);
                if (before != null) ops.Move(w.Handle, before.Value, out _);
                spanned = false; stripped = false;   // bordered at its old bounds now, even if it was spanned before this attempt
                return Set(SpanState.Refused, err != 0 ? MoveFailed(err) : MoveDidNotTake(kept));
            }
            spanned = true; stripped = true;
            spannedPlan = plan;
            log.Write("spanned " + plan.Window.Width + "x" + plan.Window.Height + " (" + plan.Mode + ")");
            Set(SpanState.Spanned, SpannedWords(plan.Window, false), plan, plan.Window);   // before the event, so a handler reading Status sees Spanned
            Raise(plan, w.Handle);
            return Status;
        }

        private void Raise(PlanResult plan, IntPtr handle)
        {
            try { Spanned?.Invoke(plan, handle); }
            catch (Exception ex) { log.Write("topology handler failed: " + ex.GetType().Name); }   // the window is spanned either way
        }

        public SpanStatus RestoreNow()
        {
            if (window == IntPtr.Zero) return Set(SpanState.Refused, NotRunning);
            // Ruling S-R19: the rule this window was found with, never one that reads every window's details.
            WindowInfo? w = ops.TopLevelWindows(foundWith).FirstOrDefault(x => x.Handle == window);
            if (!spanned && before == null && w != null && w.HasCaption) return Set(SpanState.Found, NothingToRestore);
            Rect? to = Fit(before ?? DefaultBounds, ops.Displays());
            if (to == null) return Set(SpanState.Refused, NoDisplay);
            RestoreTo(to.Value);
            autoDone = true;
            ops.Focus(window);
            return Set(SpanState.Restored, RestoredWords);
        }

        private void RestoreTo(Rect to)
        {
            ops.RestoreBorder(window, out _);
            ops.Move(window, to, out _);
            spanned = false; stripped = false;
            log.Write("restored to " + to.Width + "x" + to.Height);
        }

        /// <summary>The configured Mainhand's work area, else the primary's, else any; null when Windows reports no display.</summary>
        private Rect? SurvivingWorkArea(TrayConfig config)
        {
            IReadOnlyList<DisplayInfo> displays = ops.Displays();
            if (displays.Count == 0) return null;
            DisplayInfo d = displays.FirstOrDefault(x => x.Id == config.CanvasMainhand) ?? displays.FirstOrDefault(x => x.IsPrimary) ?? displays[0];
            return d.WorkArea;
        }

        /// <summary>Inside a monitor's work area: the one holding the rect's top-left, else the primary; shrunk to fit, nudged inside.
        /// Null when Windows reports no display.</summary>
        private static Rect? Fit(Rect r, IReadOnlyList<DisplayInfo> displays)
        {
            if (displays.Count == 0) return null;
            DisplayInfo d = displays.FirstOrDefault(x => r.Left >= x.Bounds.Left && r.Left < x.Bounds.Right && r.Top >= x.Bounds.Top && r.Top < x.Bounds.Bottom)
                ?? displays.FirstOrDefault(x => x.IsPrimary) ?? displays[0];
            Rect wa = d.WorkArea;
            int w = Math.Min(r.Width, wa.Width), h = Math.Min(r.Height, wa.Height);
            int l = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w)), t = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
            return new Rect(l, t, w, h);
        }
    }
}
