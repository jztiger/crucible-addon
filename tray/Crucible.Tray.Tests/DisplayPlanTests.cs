using System.Collections.Generic;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>Which rectangles the WoW window and the addon get, from the displays Windows reports and the person's choice (spec §6.1).</summary>
    public class DisplayPlanTests
    {
        private static DisplayInfo D(string id, int x, int y, int w, int h, bool primary = false) =>
            new DisplayInfo { Id = id, Name = "\\\\.\\DISPLAY" + id, Bounds = new Rect(x, y, w, h), WorkArea = new Rect(x, y, w, h - 48), IsPrimary = primary };

        // Modelled on the owner's desk, in DPI-scaled sizes (its physical pixels are in TheOwnersPhysicalPair below): two 2560x1440
        // side by side (primary at 0, the other at -2560), one 2048x1280 below, one portrait 1440x2560 far right.
        private static readonly List<DisplayInfo> Owner = new List<DisplayInfo>
        {
            D("P", 0, 0, 2560, 1440, true), D("L", -2560, 0, 2560, 1440), D("B", 838, 2160, 2048, 1280), D("T", 3840, -390, 1440, 2560),
        };
        private static CanvasChoice Dual(string a, string b, string mainhand) => new CanvasChoice { Mode = "DUAL", DisplayIds = new[] { a, b }, MainhandId = mainhand };

        [Fact]
        public void TwoSideBySide_MainhandOnTheRight()
        {
            PlanResult p = DisplayPlan.Plan(Owner, Dual("L", "P", "P"));
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(new Rect(-2560, 0, 5120, 1440), p.Window);
            Assert.Equal(new Rect(2560, 0, 2560, 1440), p.Mainhand);   // window pixels, bottom-left origin
            Assert.Equal(new Rect(0, 0, 2560, 1440), p.Workspace);
            Assert.Equal("P", p.MainhandId); Assert.Equal("L", p.WorkspaceId); Assert.Equal("DUAL", p.Mode);
        }

        [Fact]
        public void TheOwnersPhysicalPair_DISPLAY3AndDISPLAY2_Spans6400x2160()
        {
            // The owner's desk in physical pixels, as the selftest reports it (ruling S-R6): DISPLAY2 = P, DISPLAY3 = L,
            // DISPLAY5 = B, DISPLAY1 = T. B only touches the box's bottom edge and T its right edge, so the pair is not refused.
            var physical = new List<DisplayInfo> { D("P", 0, 0, 3840, 2160, true), D("L", -2560, 0, 2560, 1440), D("B", 838, 2160, 2560, 1600), D("T", 3840, -390, 1440, 2560) };
            PlanResult p = DisplayPlan.Plan(physical, Dual("L", "P", "P"));
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(new Rect(-2560, 0, 6400, 2160), p.Window);
            Assert.Equal(new Rect(2560, 0, 3840, 2160), p.Mainhand);
            Assert.Equal(new Rect(0, 720, 2560, 1440), p.Workspace);   // the shorter screen is the top 1440 px: y from the bottom = 2160 - 1440
        }

        [Fact]
        public void UnequalHeightsAndAVerticalOffset_TheWindowIsTheBoundingBox_AndYIsFromTheBottom()
        {
            // the primary and the 2048x1280 below it (offset 838 px right, starting 720 px below the primary's bottom)
            PlanResult p = DisplayPlan.Plan(Owner, Dual("P", "B", "P"));
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(new Rect(0, 0, 2886, 3440), p.Window);
            Assert.Equal(new Rect(0, 2000, 2560, 1440), p.Mainhand);   // top display: y from bottom = 3440 - 1440
            Assert.Equal(new Rect(838, 0, 2048, 1280), p.Workspace);
        }

        [Fact]
        public void NegativeCoordinatesAndAVoid_StillInsideTheWindow()
        {
            // The owner's P and T without B: on the full desk this pair is refused, because its box reaches B (ruling P2-R14).
            var desk = new List<DisplayInfo> { Owner[0], Owner[1], Owner[3] };
            PlanResult p = DisplayPlan.Plan(desk, Dual("P", "T", "P"));
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(new Rect(0, -390, 5280, 2560), p.Window);   // T runs from -390 to 2170, so 2170 - (-390) tall
            Assert.Equal(new Rect(0, 730, 2560, 1440), p.Mainhand);    // y from bottom = 2170 - 1440
            Assert.Equal(new Rect(3840, 0, 1440, 2560), p.Workspace);
        }

        [Fact]
        public void Split_HalvesOneDisplay_GameOnTheChosenSide()
        {
            var choice = new CanvasChoice { Mode = "SPLIT", DisplayIds = new[] { "P" }, SplitSide = "right" };
            PlanResult p = DisplayPlan.Plan(Owner, choice);
            Assert.True(p.Ok, p.Reason);
            Assert.Equal(new Rect(0, 0, 2560, 1440), p.Window);
            Assert.Equal(new Rect(1280, 0, 1280, 1440), p.Mainhand);
            Assert.Equal(new Rect(0, 0, 1280, 1440), p.Workspace);
            Assert.Equal("SPLIT", p.Mode); Assert.Equal("P", p.MainhandId); Assert.Equal("P", p.WorkspaceId);
        }

        [Theory]
        [InlineData("DUAL", new[] { "L", "X" }, "L", "your second display is not connected")]
        [InlineData("DUAL", new[] { "X", "P" }, "X", "the Mainhand display is not connected")]
        [InlineData("SPLIT", new[] { "X" }, null, "the chosen display is not connected")]
        [InlineData("DUAL", new[] { "L", "L" }, "L", "the same display twice")]
        [InlineData("DUAL", new[] { "L" }, "L", "pick two displays")]
        [InlineData("DUAL", new[] { "L", "P", "B" }, "L", "pick two displays")]
        [InlineData("DUAL", new[] { "L", "P" }, "B", "the Mainhand must be one of the two")]
        [InlineData("DUAL", new[] { "L", "P" }, null, "choose the Mainhand display")]
        [InlineData("SPLIT", new[] { "L", "P" }, null, "split mode takes one display")]
        [InlineData("SPLIT", new string[0], null, "split mode takes one display")]
        [InlineData("WHAT", new[] { "L", "P" }, "L", "unknown display mode")]
        public void Refusals(string mode, string[] ids, string? mainhand, string reason)
        {
            PlanResult p = DisplayPlan.Plan(Owner, new CanvasChoice { Mode = mode, DisplayIds = ids, MainhandId = mainhand });
            Assert.False(p.Ok);
            Assert.Equal(reason, p.Reason);
            Assert.DoesNotContain("X", p.Reason.Replace("WHAT", ""));   // ruling P2-R15: a role, never an id
        }

        [Fact]
        public void AZeroSizedDisplayIsRefused()
        {
            var displays = new List<DisplayInfo> { D("A", 0, 0, 0, 1440), D("B", 0, 0, 2560, 1440) };
            PlanResult p = DisplayPlan.Plan(displays, Dual("A", "B", "A"));
            Assert.False(p.Ok); Assert.Equal("the Mainhand display is empty", p.Reason);
            Assert.Equal("your second display is empty", DisplayPlan.Plan(displays, Dual("B", "A", "B")).Reason);
        }

        [Fact]
        public void RectsNeverOverlapAndFitTheWindow_ForEveryPairThatPlans()
        {
            string[] ids = { "P", "L", "B", "T" };
            var planned = new List<string>();
            foreach (string a in ids) foreach (string b in ids)
            {
                if (a == b) continue;
                PlanResult p = DisplayPlan.Plan(Owner, Dual(a, b, a));
                if (!p.Ok) { Assert.StartsWith("the span would also cover", p.Reason); continue; }
                planned.Add(a + "+" + b);
                Assert.True(p.Mainhand.Left >= 0 && p.Mainhand.Top >= 0 && p.Mainhand.Right <= p.Window.Width && p.Mainhand.Bottom <= p.Window.Height);
                Assert.True(p.Workspace.Left >= 0 && p.Workspace.Top >= 0 && p.Workspace.Right <= p.Window.Width && p.Workspace.Bottom <= p.Window.Height);
                bool apart = p.Mainhand.Right <= p.Workspace.Left || p.Workspace.Right <= p.Mainhand.Left || p.Mainhand.Bottom <= p.Workspace.Top || p.Workspace.Bottom <= p.Mainhand.Top;
                Assert.True(apart, a + "+" + b);
            }
            // Only the two pairs whose box holds no third display (ruling P2-R14).
            Assert.Equal(new[] { "P+L", "P+B", "L+P", "B+P" }, planned);
        }

        [Theory]
        [InlineData("L", "P", null)]                 // the owner's own pair: nothing else inside the box
        [InlineData("P", "T", "\\\\.\\DISPLAYB")]    // the box reaches 10 px into B's top
        [InlineData("L", "B", "\\\\.\\DISPLAYP")]
        [InlineData("L", "T", "\\\\.\\DISPLAYP")]
        public void APairWhoseBoxCoversAThirdDisplay_IsRefused_ByItsName(string a, string b, string? covered)
        {
            PlanResult p = DisplayPlan.Plan(Owner, Dual(a, b, a));
            if (covered == null) { Assert.True(p.Ok, p.Reason); return; }
            Assert.False(p.Ok);
            Assert.Equal("the span would also cover " + covered + " - pick another pair", p.Reason);
        }

        [Fact]
        public void ADisplayThatOnlyTouchesTheBox_IsNotCovered()
        {
            var desk = new List<DisplayInfo> { D("A", 0, 0, 1920, 1080), D("B", 1920, 0, 1920, 1080), D("C", 0, 1080, 3840, 1080) };
            Assert.True(DisplayPlan.Plan(desk, Dual("A", "B", "A")).Ok);
        }

        [Fact]
        public void EveryReasonFitsTheTooltip()
        {
            // Ruling P2-R15: at most SpanController.MaxWords, with the longest display name Windows hands out.
            var desk = new List<DisplayInfo> { D("A", 0, 0, 1920, 1080), D("B", 3840, 0, 1920, 1080), new DisplayInfo { Id = "C", Name = "\\\\.\\DISPLAY999", Bounds = new Rect(1920, 0, 1920, 1080) } };
            string covered = DisplayPlan.Plan(desk, Dual("A", "B", "A")).Reason;
            Assert.Contains("DISPLAY999", covered);
            Assert.True(covered.Length <= SpanController.MaxWords, covered);
        }
    }
}
