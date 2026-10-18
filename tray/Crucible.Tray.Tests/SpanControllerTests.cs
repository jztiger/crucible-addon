using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Crucible.Tray.Tests
{
    internal sealed class FakeWindowOps : IWindowOps
    {
        public List<DisplayInfo> Screens = new List<DisplayInfo>
        {
            new DisplayInfo { Id = "P", Name = "\\\\.\\DISPLAY2", Bounds = new Rect(0, 0, 2560, 1440), WorkArea = new Rect(0, 0, 2560, 1392), IsPrimary = true },
            new DisplayInfo { Id = "L", Name = "\\\\.\\DISPLAY3", Bounds = new Rect(-2560, 0, 2560, 1440), WorkArea = new Rect(-2560, 0, 2560, 1392) },
        };
        public List<WindowInfo> Windows = new List<WindowInfo>();   // each open window's handle, program and title
        public Dictionary<IntPtr, Rect> Bounds = new Dictionary<IntPtr, Rect>();
        public Dictionary<IntPtr, bool> Captions = new Dictionary<IntPtr, bool>();
        public Dictionary<IntPtr, bool> Minimized = new Dictionary<IntPtr, bool>();
        public Dictionary<IntPtr, bool> Maximized = new Dictionary<IntPtr, bool>();
        private int nextHandle = 1;   // never reused, so a new WoW process is a new window even with no tick in between
        public int FailWith; public bool MoveLies; public List<string> Calls = new List<string>();
        /// <summary>Every handle whose title, bounds, caption or state was read, in order (ruling S-R19).</summary>
        public List<IntPtr> DetailsRead = new List<IntPtr>();
        public IReadOnlyList<DisplayInfo> Displays() => Screens;
        /// <summary>The shell's order: the program path, the core's rule (an exception counts as "not WoW"), and only for a
        /// program the rule accepts the title, then the bounds, the caption and the state.</summary>
        public IReadOnlyList<WindowInfo> TopLevelWindows(Func<string, bool> isWowProgram)
        {
            var list = new List<WindowInfo>();
            foreach (WindowInfo w in Windows)
            {
                if (w.ExePath.Length == 0) continue;
                bool wow;
                try { wow = isWowProgram(w.ExePath); }
                catch (Exception) { wow = false; }
                if (!wow) continue;
                DetailsRead.Add(w.Handle);
                if (w.Title.Length == 0) continue;
                list.Add(new WindowInfo
                {
                    Handle = w.Handle, ExePath = w.ExePath, Title = w.Title, Bounds = Bounds[w.Handle], HasCaption = Captions[w.Handle],
                    Minimized = Minimized.TryGetValue(w.Handle, out bool m) && m, Maximized = Maximized.TryGetValue(w.Handle, out bool x) && x,
                });
            }
            return list;
        }
        /// <summary>Where Windows parks a minimised window: its bounds mean nothing.</summary>
        public static readonly Rect Parked = new Rect(-32000, -32000, 160, 28);
        public void Minimise(IntPtr h) { Minimized[h] = true; Bounds[h] = Parked; }
        public void BringBack(IntPtr h, Rect at) { Minimized[h] = false; Bounds[h] = at; }
        /// <summary>The handle of every strip, border, move and focus call, beside <see cref="Calls"/>.</summary>
        public List<IntPtr> CalledOn = new List<IntPtr>();
        public bool StripBorder(IntPtr h, out int error) { Calls.Add("strip"); CalledOn.Add(h); error = FailWith; if (FailWith != 0) return false; Captions[h] = false; return true; }
        public bool RestoreBorder(IntPtr h, out int error) { Calls.Add("border"); CalledOn.Add(h); error = 0; Captions[h] = true; return true; }
        public Rect? NextMoveLandsAt;   // one move ends somewhere else (Windows clamping it), the next moves are honest
        public bool Move(IntPtr h, Rect to, out int error)
        {
            Calls.Add("move " + to); CalledOn.Add(h); error = 0;
            if (NextMoveLandsAt != null) { Bounds[h] = NextMoveLandsAt.Value; NextMoveLandsAt = null; }
            else if (!MoveLies) Bounds[h] = to;
            return true;
        }
        public Rect? BoundsOf(IntPtr h) => Bounds.TryGetValue(h, out Rect r) ? r : (Rect?)null;
        public void Focus(IntPtr h) { Calls.Add("focus"); CalledOn.Add(h); }
        public WindowInfo AddWow(string exe, Rect at, bool caption = true) => AddWindow(exe, at, "World of Warcraft", caption);
        /// <summary>Any program's top-level window, WoW's or another's.</summary>
        public WindowInfo AddWindow(string exe, Rect at, string title, bool caption = true)
        {
            var w = new WindowInfo { Handle = (IntPtr)nextHandle++, ExePath = exe, Title = title };
            Windows.Add(w); Bounds[w.Handle] = at; Captions[w.Handle] = caption; return w;
        }
    }

    public class SpanControllerTests
    {
        private static readonly string Wow = Path.Combine(Path.GetTempPath(), "WoW");
        private static TrayConfig Config(bool auto = false) => new TrayConfig
        {
            WowFolder = Wow, Products = new[] { "_classic_beta_" }, CanvasEnabled = true, CanvasMode = "DUAL",
            CanvasDisplays = new[] { "L", "P" }, CanvasMainhand = "P", CanvasAutoSpan = auto, CanvasSpanDelaySeconds = 10,
        };
        private DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        private SpanController Make(FakeWindowOps ops) => new SpanController(ops, () => now, new TrayLog(Path.Combine(Path.GetTempPath(), "span-" + Guid.NewGuid().ToString("N") + ".txt")));
        private static string Exe(string name = "WowB.exe", string product = "_classic_beta_") => Path.Combine(Wow, product, name);

        [Theory]
        [InlineData("WowB.exe", "_classic_beta_", true)]
        [InlineData("Wow.exe", "_classic_beta_", true)]
        [InlineData("Wow.exe", "_retail_", false)]        // not a listed product
        [InlineData("Agent.exe", "_classic_beta_", true)]  // any exe directly inside the product folder counts; the title and size decide the rest
        public void IsWowWindow_IsThePathRule(string name, string product, bool expected)
        {
            var w = new WindowInfo { ExePath = Exe(name, product), Title = "World of Warcraft" };
            Assert.Equal(expected, SpanController.IsWowWindow(w, Wow, new[] { "_classic_beta_" }));
        }

        [Fact]
        public void ANestedPathOrAnotherFolderIsNotWow()
        {
            Assert.False(SpanController.IsWowWindow(new WindowInfo { ExePath = Path.Combine(Wow, "_classic_beta_", "Utils", "x.exe"), Title = "t" }, Wow, new[] { "_classic_beta_" }));
            Assert.False(SpanController.IsWowWindow(new WindowInfo { ExePath = Path.Combine(Path.GetTempPath(), "Other", "_classic_beta_", "Wow.exe"), Title = "t" }, Wow, new[] { "_classic_beta_" }));
            Assert.False(SpanController.IsWowWindow(new WindowInfo { ExePath = Exe(), Title = "" }, Wow, new[] { "_classic_beta_" }));
        }

        [Fact]
        public void NothingHappensUntilTheSwitchIsOn()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true); cfg.CanvasEnabled = false;
            Assert.Equal(SpanState.Idle, c.Tick(cfg).State); now = now.AddSeconds(30); Assert.Equal(SpanState.Idle, c.Tick(cfg).State);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void SpanNow_StripsMovesVerifiesAndRaisesSpanned()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); PlanResult? heard = null; c.Spanned += (p, h) => heard = p;
            c.Tick(Config());
            SpanStatus s = c.SpanNow(Config());
            Assert.Equal(SpanState.Spanned, s.State);
            Assert.Equal(new Rect(-2560, 0, 5120, 1440), ops.Bounds[w.Handle]);
            Assert.Equal(new[] { "strip", "move 5120x1440 at -2560,0" }, ops.Calls);
            Assert.NotNull(heard); Assert.Equal("P", heard!.MainhandId);
            Assert.Contains("5120x1440", s.Words);
        }

        [Fact]
        public void AutoSpan_WaitsTheDelay_ThenOncePerWindow()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true);
            Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            now = now.AddSeconds(9); Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            now = now.AddSeconds(2); Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
            int calls = ops.Calls.Count; now = now.AddSeconds(60); c.Tick(cfg); Assert.Equal(calls, ops.Calls.Count);
        }

        [Fact]
        public void RefusedWhenADisplayIsMissing_OrFullscreenWindowed_OrNoWindow()
        {
            var ops = new FakeWindowOps(); var c = Make(ops);
            Assert.Equal(SpanState.Refused, c.SpanNow(Config()).State); Assert.Contains("not running", c.Status.Words);
            ops.AddWow(Exe(), new Rect(0, 0, 2560, 1440), caption: false);   // exactly one display, no caption
            c.Tick(Config()); Assert.Equal(SpanState.Refused, c.SpanNow(Config()).State); Assert.Contains("Windowed", c.Status.Words);
            ops.Captions[(IntPtr)1] = true; var cfg = Config(); cfg.CanvasDisplays = new[] { "X", "P" };   // the Mainhand (P) stays in the pair, so the plan reaches the missing-display check
            c.Tick(cfg); Assert.Equal(SpanState.Refused, c.SpanNow(cfg).State); Assert.Equal("your second display is not connected", c.Status.Words);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void AMoveThatDidNotTake_IsUndone_AndReported()
        {
            var ops = new FakeWindowOps { MoveLies = true }; var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); c.Tick(Config());
            SpanStatus s = c.SpanNow(Config());
            Assert.Equal(SpanState.Refused, s.State); Assert.Contains("did not take", s.Words);
            Assert.True(ops.Captions[w.Handle]); // border back
        }

        [Fact]
        public void Error5_SaysPrivilegeLevel()
        {
            var ops = new FakeWindowOps { FailWith = 5 }; ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); c.Tick(Config());
            Assert.Contains("same privilege level", c.SpanNow(Config()).Words);
        }

        [Fact]
        public void Restore_PutsTheBorderAndBoundsBack_FittedToAWorkArea_AndPausesAutoSpan()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 3000, 2000));
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg); c.SpanNow(cfg);
            SpanStatus s = c.RestoreNow();
            Assert.Equal(SpanState.Restored, s.State);
            Assert.True(ops.Captions[w.Handle]);
            Rect back = ops.Bounds[w.Handle];
            Assert.True(back.Width <= 2560 && back.Height <= 1392 && back.Left >= 0 && back.Top >= 0, back.ToString());
            Assert.Contains("focus", ops.Calls);
            now = now.AddSeconds(60); Assert.Equal(SpanState.Restored, c.Tick(cfg).State); // not spanned again by itself
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);                          // the button still works
        }

        [Fact]
        public void ADisplayVanishingWhileSpanned_RestoresToTheSurvivingMainhand_AndRefusesUntilItIsBack()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg); c.SpanNow(cfg);
            ops.Screens.RemoveAll(d => d.Id == "L");
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Refused, s.State); Assert.Equal("your second display is not connected", s.Words);
            Assert.True(ops.Captions[w.Handle]); Assert.True(ops.Bounds[w.Handle].Width <= 2560);
            now = now.AddSeconds(60); Assert.Equal(SpanState.Refused, c.Tick(cfg).State);
            ops.Screens.Add(new DisplayInfo { Id = "L", Name = "\\\\.\\DISPLAY3", Bounds = new Rect(-2560, 0, 2560, 1440), WorkArea = new Rect(-2560, 0, 2560, 1392) });
            int calls = ops.Calls.Count;
            s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Contains("press Span", s.Words);
            Assert.Equal(calls, ops.Calls.Count);                                            // no retry by itself
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);
        }

        [Fact]
        public void AFullscreenRefusal_FollowsTheFix_WithoutRetryingByItself()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(0, 0, 2560, 1440), caption: false);
            var c = Make(ops); var cfg = Config(auto: true);
            c.Tick(cfg); Assert.Contains("Windowed", c.SpanNow(cfg).Words);
            now = now.AddSeconds(5); Assert.Contains("Windowed", c.Tick(cfg).Words);          // still fullscreen: still refused
            ops.Captions[(IntPtr)1] = true; ops.Bounds[(IntPtr)1] = new Rect(100, 100, 1920, 1080);   // the player switched to Windowed
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Contains("press Span", s.Words);
            now = now.AddSeconds(60); s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Equal("WoW found - press Span WoW now", s.Words);   // the attempt is spent: the words keep saying so
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void APlanRefusal_FollowsTheChoice()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); cfg.CanvasDisplays = new[] { "P" };
            c.Tick(cfg); Assert.Contains("pick two displays", c.SpanNow(cfg).Words);
            cfg.CanvasDisplays = new[] { "L", "P" };
            Assert.Equal(SpanState.Found, c.Tick(cfg).State);
        }

        [Fact]
        public void APrivilegeRefusal_StaysOnTheStatusLine()
        {
            var ops = new FakeWindowOps { FailWith = 5 }; ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); c.Tick(Config()); c.SpanNow(Config());
            now = now.AddSeconds(2); Assert.Contains("same privilege level", c.Tick(Config()).Words);
        }

        [Fact]
        public void SwitchingCanvasOffWhileSpanned_RestoresTheWindow_AndOnAgainIsFound()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg); c.SpanNow(cfg);
            cfg.CanvasEnabled = false;
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Idle, s.State); Assert.Equal("Crucible Canvas is off", s.Words);
            Assert.True(ops.Captions[w.Handle]);
            Rect back = ops.Bounds[w.Handle];
            Assert.True(back.Left >= 0 && back.Top >= 0 && back.Right <= 2560 && back.Bottom <= 1392, back.ToString());
            cfg.CanvasEnabled = true;
            Assert.Equal(SpanState.Found, c.Tick(cfg).State);
        }

        [Fact]
        public void NoDisplayReported_NothingIsRestored_AndNothingThrows()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            var saved = ops.Screens.ToList(); ops.Screens.Clear(); int calls = ops.Calls.Count;
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Refused, s.State); Assert.Contains("no display reported - nothing restored", s.Words);
            Assert.Contains("nothing restored", c.RestoreNow().Words);
            var off = Config(); off.CanvasEnabled = false; Assert.Contains("nothing restored", c.Tick(off).Words);
            Assert.Equal(calls, ops.Calls.Count);
            Assert.Equal(new Rect(-2560, 0, 5120, 1440), ops.Bounds[w.Handle]); Assert.False(ops.Captions[w.Handle]);
            ops.Screens.AddRange(saved);
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);                               // the window never moved
        }

        [Fact]
        public void TheSpannedEvent_SeesSpanned_AndAThrowingHandlerDoesNotUndoIt()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); SpanState? seen = null;
            c.Spanned += (p, h) => { seen = c.Status.State; throw new IOException("disk full"); };
            c.Tick(Config());
            Assert.Equal(SpanState.Spanned, c.SpanNow(Config()).State);
            Assert.Equal(SpanState.Spanned, seen); Assert.Equal(SpanState.Spanned, c.Status.State);
        }

        [Fact]
        public void AMoveThatLandedElsewhere_ReportsWhereItLanded_BeforeTheUndo()
        {
            var ops = new FakeWindowOps { NextMoveLandsAt = new Rect(0, 0, 3840, 1440) }; var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); c.Tick(Config());
            SpanStatus s = c.SpanNow(Config());
            Assert.Equal(SpanState.Refused, s.State); Assert.Contains("WoW kept 3840x1440 at 0,0", s.Words);
            Assert.Equal(new Rect(100, 100, 1920, 1080), ops.Bounds[w.Handle]); Assert.True(ops.Captions[w.Handle]);
        }

        [Fact]
        public void AFailedReSpan_LeavesTheWindowNotSpanned()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            var split = Config(); split.CanvasMode = "SPLIT"; split.CanvasDisplays = new[] { "L" };
            ops.NextMoveLandsAt = new Rect(1, 1, 10, 10);
            Assert.Equal(SpanState.Refused, c.SpanNow(split).State);
            Assert.True(ops.Captions[w.Handle]); Assert.Equal(new Rect(100, 100, 1920, 1080), ops.Bounds[w.Handle]);
            int calls = ops.Calls.Count;
            ops.Screens.RemoveAll(d => d.Id == "L");
            c.Tick(split);
            Assert.Equal(calls, ops.Calls.Count);   // not spanned, so a vanished display restores nothing
        }

        [Fact]
        public void RestoreNow_OnANeverSpannedBorderedWindow_DoesNothing()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); c.Tick(Config());
            SpanStatus s = c.RestoreNow();
            Assert.Equal(SpanState.Found, s.State); Assert.Equal("nothing to restore", s.Words);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void ANewWowProcessAfterARestore_IsAutoSpannedAgain()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg); c.SpanNow(cfg); c.RestoreNow();
            ops.Windows.Clear(); c.Tick(cfg); Assert.Equal(SpanState.Idle, c.Status.State);
            ops.AddWow(Exe(), new Rect(0, 0, 1920, 1080)); c.Tick(cfg); now = now.AddSeconds(11);
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
        }

        // ---- final fix wave (rulings P2-R9, P2-R10, P2-R13, P2-R15) ----------------------------------------------------

        [Fact]
        public void ASpannedWindowThatWoWMoved_StepsDown_AndIsNotSpannedAgainByItself()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg); c.SpanNow(cfg);
            int calls = ops.Calls.Count;
            ops.Bounds[w.Handle] = new Rect(200, 100, 1920, 1080);   // WoW put its frame back (or Windows moved it)
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Equal("WoW moved its window - press Span WoW now", s.Words);
            now = now.AddSeconds(60); s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Equal("WoW found - press Span WoW now", s.Words);
            Assert.Equal(calls, ops.Calls.Count);
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);
        }

        [Fact]
        public void ASpannedWindow_StepsDown_WhenTheChoiceChanges_EvenWithTheSameBox()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            int calls = ops.Calls.Count;
            cfg.CanvasMainhand = "L";                              // Mainhand swapped: the box is the same 5120x1440
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Equal("the displays changed - press Span WoW now", s.Words);
            Assert.Equal(calls, ops.Calls.Count);

            var split = Config(); split.CanvasMode = "SPLIT"; split.CanvasDisplays = new[] { "P" }; split.CanvasSplitSide = "left";
            Assert.Equal(SpanState.Spanned, c.SpanNow(split).State);
            split.CanvasSplitSide = "right";                       // the game side changed: the window is the same display
            Assert.Equal("the displays changed - press Span WoW now", c.Tick(split).Words);
        }

        [Fact]
        public void ASpannedWindow_StepsDown_WhenThePairChanges()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            ops.Screens.Add(new DisplayInfo { Id = "R", Name = "\\\\.\\DISPLAY4", Bounds = new Rect(2560, 0, 2560, 1440), WorkArea = new Rect(2560, 0, 2560, 1392) });
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            cfg.CanvasDisplays = new[] { "P", "R" };
            Assert.Equal("the displays changed - press Span WoW now", c.Tick(cfg).Words);
        }

        [Fact]
        public void SwitchingCanvasOffAfterAStepDown_StillPutsTheWindowBack()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            cfg.CanvasMainhand = "L"; Assert.Equal(SpanController.DisplaysChanged, c.Tick(cfg).Words);
            cfg.CanvasEnabled = false;
            Assert.Equal(SpanState.Idle, c.Tick(cfg).State);
            Assert.True(ops.Captions[w.Handle]);                   // still borderless after the step-down: P2-R3 puts it back
            Assert.Equal(new Rect(100, 100, 1920, 1080), ops.Bounds[w.Handle]);
        }

        [Fact]
        public void AStepDownFromSplitToDual_SpansOnTheNextClick_NotRefusedAsFullscreen()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops);
            var split = Config(); split.CanvasMode = "SPLIT"; split.CanvasDisplays = new[] { "P" };
            c.Tick(split); Assert.Equal(SpanState.Spanned, c.SpanNow(split).State);   // caption-less at exactly P's bounds now
            var dual = Config();
            Assert.Equal(SpanController.DisplaysChanged, c.Tick(dual).Words);
            Assert.Equal(SpanState.Spanned, c.SpanNow(dual).State);                    // our own strip, not Windowed (Fullscreen)
            Assert.Equal(new Rect(-2560, 0, 5120, 1440), ops.Bounds[w.Handle]);
        }

        [Fact]
        public void ADisplayVanishingAfterAStepDown_StillRestoresTheWindow()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            cfg.CanvasMainhand = "L"; Assert.Equal(SpanController.DisplaysChanged, c.Tick(cfg).Words);
            ops.Screens.RemoveAll(d => d.Id == "L");
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Refused, s.State); Assert.Equal("the Mainhand display is not connected", s.Words);
            Assert.True(ops.Captions[w.Handle]); Assert.True(ops.Bounds[w.Handle].Width <= 2560);
        }

        [Fact]
        public void AMinimisedSpannedWindow_IsStillSpanned()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(); c.Tick(cfg); c.SpanNow(cfg);
            int calls = ops.Calls.Count;
            ops.Minimise(w.Handle);
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);   // a parked window's bounds mean nothing
            ops.BringBack(w.Handle, new Rect(-2560, 0, 5120, 1440));
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
            Assert.Equal(calls, ops.Calls.Count);
        }

        [Fact]
        public void AWindowAlreadySpanned_IsAdopted_AfterATrayRestart_WithoutAnyCall()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(-2560, 0, 5120, 1440), caption: false);
            var c = Make(ops); PlanResult? heard = null; c.Spanned += (p, h) => heard = p;
            var cfg = Config(auto: true);
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Spanned, s.State); Assert.Equal("Spanned 5120x1440 (adopted)", s.Words);
            Assert.NotNull(heard);                                 // the shell writes Topology.lua for it
            now = now.AddSeconds(60); Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void SplitInWindowedFullscreen_OnTheChosenDisplay_IsAdopted()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(0, 0, 2560, 1440), caption: false);
            var c = Make(ops); var cfg = Config(); cfg.CanvasMode = "SPLIT"; cfg.CanvasDisplays = new[] { "P" };
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Spanned, s.State); Assert.Equal("Spanned 2560x1440 (adopted)", s.Words);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void ACaptionlessWindowOnAnotherDisplay_IsStillRefused()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(-2560, 0, 2560, 1440), caption: false);   // fullscreen on L
            var c = Make(ops); var cfg = Config(); cfg.CanvasMode = "SPLIT"; cfg.CanvasDisplays = new[] { "P" };
            Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            SpanStatus s = c.SpanNow(cfg);
            Assert.Equal(SpanState.Refused, s.State); Assert.Contains("Windowed", s.Words);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void AMinimisedWindow_IsRefused_AndItsParkedBoundsAreNeverRemembered()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            ops.Minimise(w.Handle);
            var c = Make(ops); var cfg = Config(); c.Tick(cfg);
            SpanStatus s = c.SpanNow(cfg);
            Assert.Equal(SpanState.Refused, s.State); Assert.Equal("bring WoW back up first", s.Words);
            Assert.Empty(ops.Calls);
            ops.BringBack(w.Handle, new Rect(100, 100, 1920, 1080));
            Assert.Contains("press Span", c.Tick(cfg).Words);      // the words follow the fix
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);
            c.RestoreNow();
            Assert.Equal(new Rect(100, 100, 1920, 1080), ops.Bounds[w.Handle]);   // the real rect, not the parked one
        }

        [Fact]
        public void AutoSpan_WaitsWhileTheWindowIsMinimised()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            ops.Minimise(w.Handle);
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg);
            now = now.AddSeconds(30); Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            Assert.Empty(ops.Calls);
            ops.BringBack(w.Handle, new Rect(100, 100, 1920, 1080));
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
        }

        // UAT row 44 (owner: refuse): a span under WS_MAXIMIZE leaves WoW drawing its old size after Restore. A maximised
        // bordered window sits on its display's work area plus Windows' invisible 8 px frame.
        private static readonly Rect MaximisedOnP = new Rect(-8, -8, 2576, 1408);

        [Fact]
        public void AMaximisedWindow_IsRefused_WithTheWordsToRestoreIt_AndNothingIsCalled()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), MaximisedOnP);
            ops.Maximized[w.Handle] = true;
            var c = Make(ops); var cfg = Config(); c.Tick(cfg);
            SpanStatus s = c.SpanNow(cfg);
            Assert.Equal(SpanState.Refused, s.State); Assert.Equal("WoW's window is maximised - restore it from its title bar", s.Words);
            Assert.Empty(ops.Calls);
            now = now.AddSeconds(5); s = c.Tick(cfg);                  // still maximised: still refused, looked at again
            Assert.Equal(SpanState.Refused, s.State); Assert.Equal("WoW's window is maximised - restore it from its title bar", s.Words);
            Assert.Empty(ops.Calls);
        }

        [Fact]
        public void AMaximisedRefusal_FollowsTheFix_AndSpanThenMovesIt_RememberingTheUnMaximisedBounds()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), MaximisedOnP);
            ops.Maximized[w.Handle] = true;
            var c = Make(ops); var cfg = Config(); c.Tick(cfg);
            Assert.Equal(SpanState.Refused, c.SpanNow(cfg).State);
            ops.Maximized[w.Handle] = false; ops.Bounds[w.Handle] = new Rect(100, 100, 1920, 1080);   // the owner restored it down
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Equal(SpanController.FoundWindowedPressSpan, s.Words);
            Assert.Empty(ops.Calls);
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);
            Assert.Equal(new[] { "strip", "move 5120x1440 at -2560,0" }, ops.Calls);
            c.RestoreNow();
            Assert.Equal(new Rect(100, 100, 1920, 1080), ops.Bounds[w.Handle]);   // the restored-down rect, never the maximised one
        }

        [Fact]
        public void AutoSpan_WaitsWhileTheWindowIsMaximised()
        {
            var ops = new FakeWindowOps(); var w = ops.AddWow(Exe(), MaximisedOnP);
            ops.Maximized[w.Handle] = true;
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg);
            now = now.AddSeconds(30); Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            Assert.Empty(ops.Calls);
            ops.Maximized[w.Handle] = false; ops.Bounds[w.Handle] = new Rect(100, 100, 1920, 1080);
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
            Assert.Equal(new Rect(-2560, 0, 5120, 1440), ops.Bounds[w.Handle]);
        }

        [Fact]
        public void ANewWindowWhileRestored_IsFound_AndAutoSpannedAgain()
        {
            var ops = new FakeWindowOps(); ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config(auto: true); c.Tick(cfg); c.SpanNow(cfg); c.RestoreNow();
            ops.Windows.Clear(); ops.AddWow(Exe(), new Rect(0, 0, 1920, 1080));   // WoW restarted between two ticks
            SpanStatus s = c.Tick(cfg);
            Assert.Equal(SpanState.Found, s.State); Assert.Equal("WoW found - windowed", s.Words);
            now = now.AddSeconds(11); Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
        }

        [Fact]
        public void AFolderThatIsNotAPath_IsNotWow_AndDoesNotThrow()
        {
            var w = new WindowInfo { ExePath = Exe(), Title = "World of Warcraft" };
            Assert.False(SpanController.IsWowWindow(w, "C:\\Games\0WoW", new[] { "_classic_beta_" }));
        }

        // ---- part 2b final fix wave (ruling S-R19) ----------------------------------------------------------------------

        [Fact]
        public void AnotherProgramsWindow_HasOnlyItsProgramRead_WhenWoWIsFound_AndWhenItIsRestored()
        {
            // Notice v7: "To find your WoW window it looks at which program each open window belongs to, and nothing else about them."
            var ops = new FakeWindowOps();
            var other = ops.AddWindow(Path.Combine(Path.GetTempPath(), "Editor", "editor.exe"), new Rect(0, 0, 3000, 2000), "a letter to the bank");
            var nested = ops.AddWindow(Path.Combine(Wow, "_classic_beta_", "Utils", "helper.exe"), new Rect(0, 0, 3000, 2000), "a helper");
            var w = ops.AddWow(Exe(), new Rect(100, 100, 1920, 1080));
            var c = Make(ops); var cfg = Config();
            Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            Assert.Contains(w.Handle, ops.DetailsRead);
            Assert.DoesNotContain(other.Handle, ops.DetailsRead); Assert.DoesNotContain(nested.Handle, ops.DetailsRead);
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);
            ops.DetailsRead.Clear();
            Assert.Equal(SpanState.Restored, c.RestoreNow().State);
            Assert.Contains(w.Handle, ops.DetailsRead);            // RestoreNow looked again, with the same program rule
            Assert.DoesNotContain(other.Handle, ops.DetailsRead); Assert.DoesNotContain(nested.Handle, ops.DetailsRead);
        }

        [Fact]
        public void TwoWoWWindows_TheLargerIsFoundAndSpanned_TheOtherIsNeverTouched()
        {
            // Final review Minor 3. The smaller one comes first, so "the first match" would pick it.
            var ops = new FakeWindowOps();
            var small = ops.AddWow(Exe(), new Rect(100, 100, 1280, 720));
            var large = ops.AddWow(Exe(), new Rect(200, 200, 1920, 1080));
            var c = Make(ops); var cfg = Config();
            Assert.Equal(SpanState.Found, c.Tick(cfg).State);
            Assert.Equal(SpanState.Spanned, c.SpanNow(cfg).State);
            Assert.Equal(new Rect(-2560, 0, 5120, 1440), ops.Bounds[large.Handle]);
            Assert.Equal(SpanState.Spanned, c.Tick(cfg).State);
            Assert.Equal(SpanState.Restored, c.RestoreNow().State);
            Assert.True(ops.Captions[large.Handle]);
            Assert.Contains(large.Handle, ops.CalledOn);
            Assert.DoesNotContain(small.Handle, ops.CalledOn);    // no strip, move, border or focus
            Assert.Equal(new Rect(100, 100, 1280, 720), ops.Bounds[small.Handle]); Assert.True(ops.Captions[small.Handle]);
        }

        [Fact]
        public void TheProgramRule_IsC4sPathPart_AndNeverThrows()
        {
            string[] products = { "_classic_beta_" };
            Assert.True(SpanController.IsWowProgram(Exe(), Wow, products));
            Assert.False(SpanController.IsWowProgram(Path.Combine(Wow, "_classic_beta_", "Utils", "x.exe"), Wow, products));
            Assert.False(SpanController.IsWowProgram(Exe("Wow.exe", "_retail_"), Wow, new[] { "_classic_beta_", "_retail_" }));
            Assert.False(SpanController.IsWowProgram("", Wow, products));
            // The shell calls it inside the native window enumeration: what is not a path is "not WoW", never an exception.
            Assert.False(SpanController.IsWowProgram("C:\\Games\0WoW\\Wow.exe", Wow, products));
            Assert.False(SpanController.IsWowProgram(Exe(), "C:\\Games\0WoW", products));
        }

        [Fact]
        public void EveryWordFitsTheTooltip_AndNamesNoId()
        {
            Assert.True(SpanController.MaxWords < TrayText.MaxTooltip);
            var all = new List<string>();
            foreach (Type t in new[] { typeof(SpanController), typeof(DisplayPlan), typeof(Cycle) })
                all.AddRange(t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!));
            Assert.True(all.Count >= 25, "found " + all.Count);
            // The words built at run time, at their longest.
            all.Add(SpanController.CouldNotChange(int.MinValue));
            all.Add(SpanController.MoveFailed(int.MinValue));
            all.Add(SpanController.MoveDidNotTake(new Rect(-99999, -99999, 99999, 99999).ToString()));
            all.Add(SpanController.SpannedWords(new Rect(0, 0, 99999, 99999), true));
            Assert.All(all, w => Assert.True(w.Length <= SpanController.MaxWords, w.Length + ": " + w));
            Assert.All(all, w => Assert.DoesNotContain("MONITOR", w));
        }
    }
}
