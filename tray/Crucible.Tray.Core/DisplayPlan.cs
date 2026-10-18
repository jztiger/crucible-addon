using System;
using System.Collections.Generic;
using System.Linq;

namespace Crucible.Tray
{
    /// <summary>An integer rectangle. In <see cref="DisplayInfo"/> it is desktop coordinates (top-left origin, as Win32 gives
    /// them); in <see cref="PlanResult"/> it is window pixels with Top measured FROM THE WINDOW'S BOTTOM (WoW's origin).</summary>
    public readonly struct Rect : IEquatable<Rect>
    {
        public readonly int Left, Top, Width, Height;
        public Rect(int left, int top, int width, int height) { Left = left; Top = top; Width = width; Height = height; }
        public int Right => Left + Width;
        public int Bottom => Top + Height;
        public bool Equals(Rect o) => Left == o.Left && Top == o.Top && Width == o.Width && Height == o.Height;
        public override bool Equals(object? obj) => obj is Rect r && Equals(r);
        public override int GetHashCode() => Left ^ (Top << 8) ^ (Width << 16) ^ (Height << 24);
        public override string ToString() => Width + "x" + Height + " at " + Left + "," + Top;
        public static bool operator ==(Rect a, Rect b) => a.Equals(b);
        public static bool operator !=(Rect a, Rect b) => !a.Equals(b);
    }

    /// <summary>One display as Windows reports it. Id is the monitor's device-interface id (stable across reboots);
    /// Name is \\.\DISPLAYn for showing people.</summary>
    public sealed class DisplayInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public Rect Bounds { get; set; }
        public Rect WorkArea { get; set; }
        public bool IsPrimary { get; set; }
    }

    /// <summary>What the person chose in the Displays window (settings file fields canvasMode/canvasDisplays/canvasMainhand/canvasSplitSide).</summary>
    public sealed class CanvasChoice
    {
        public string Mode { get; set; } = "DUAL";
        public IReadOnlyList<string> DisplayIds { get; set; } = new string[0];
        public string? MainhandId { get; set; }
        public string SplitSide { get; set; } = "left";
    }

    public sealed class PlanResult
    {
        public bool Ok { get; set; }
        public string Reason { get; set; } = "";
        /// <summary>Desktop coordinates: where the WoW window goes.</summary>
        public Rect Window { get; set; }
        /// <summary>Window pixels, bottom-left origin.</summary>
        public Rect Mainhand { get; set; }
        public Rect Workspace { get; set; }
        public string MainhandId { get; set; } = "";
        public string WorkspaceId { get; set; } = "";
        public string Mode { get; set; } = "DUAL";
    }

    /// <summary>Spec §6.1: the window is the bounding box of the chosen displays; the two rects are their real pixels inside it.
    /// Pure, so every refusal is a unit test. Every reason names a role or a display's \\.\DISPLAYn name, never its id
    /// (ruling P2-R15), and fits <see cref="SpanController.MaxWords"/>.</summary>
    public static class DisplayPlan
    {
        public const string SplitTakesOne = "split mode takes one display";
        public const string UnknownMode = "unknown display mode";
        public const string PickTwo = "pick two displays";
        public const string SameTwice = "the same display twice";
        public const string ChooseMainhand = "choose the Mainhand display";
        public const string MainhandOfTheTwo = "the Mainhand must be one of the two";
        public const string MainhandMissing = "the Mainhand display is not connected";
        public const string SecondMissing = "your second display is not connected";
        public const string ChosenMissing = "the chosen display is not connected";
        public const string MainhandEmpty = "the Mainhand display is empty";
        public const string SecondEmpty = "your second display is empty";
        public const string ChosenEmpty = "the chosen display is empty";

        /// <summary>Ruling P2-R14: a display outside the pair that the window box would cover (shortened to fit the tooltip).</summary>
        public static string Covers(string displayName) => "the span would also cover " + displayName + " - pick another pair";

        public static PlanResult Plan(IReadOnlyList<DisplayInfo> displays, CanvasChoice choice)
        {
            PlanResult No(string why) => new PlanResult { Ok = false, Reason = why };
            if (choice.Mode == "SPLIT")
            {
                if (choice.DisplayIds.Count != 1) return No(SplitTakesOne);
                DisplayInfo? only = Find(displays, choice.DisplayIds[0]);
                if (only == null) return No(ChosenMissing);
                if (only.Bounds.Width <= 1 || only.Bounds.Height <= 0) return No(ChosenEmpty);
                int half = only.Bounds.Width / 2;
                bool left = choice.SplitSide != "right";
                var game = new Rect(left ? 0 : half, 0, left ? half : only.Bounds.Width - half, only.Bounds.Height);
                var work = new Rect(left ? half : 0, 0, left ? only.Bounds.Width - half : half, only.Bounds.Height);
                return new PlanResult { Ok = true, Window = only.Bounds, Mainhand = game, Workspace = work, MainhandId = only.Id, WorkspaceId = only.Id, Mode = "SPLIT" };
            }
            if (choice.Mode != "DUAL") return No(UnknownMode);
            if (choice.DisplayIds.Count != 2) return No(PickTwo);
            if (choice.DisplayIds[0] == choice.DisplayIds[1]) return No(SameTwice);
            if (string.IsNullOrEmpty(choice.MainhandId)) return No(ChooseMainhand);
            if (!choice.DisplayIds.Contains(choice.MainhandId)) return No(MainhandOfTheTwo);
            var chosen = new List<DisplayInfo>();
            foreach (string id in choice.DisplayIds)
            {
                bool mainhand = id == choice.MainhandId;
                DisplayInfo? d = Find(displays, id);
                if (d == null) return No(mainhand ? MainhandMissing : SecondMissing);
                if (d.Bounds.Width <= 0 || d.Bounds.Height <= 0) return No(mainhand ? MainhandEmpty : SecondEmpty);
                chosen.Add(d);
            }
            int l = chosen.Min(d => d.Bounds.Left), t = chosen.Min(d => d.Bounds.Top);
            int r = chosen.Max(d => d.Bounds.Right), b = chosen.Max(d => d.Bounds.Bottom);
            var window = new Rect(l, t, r - l, b - t);
            // Ruling P2-R14: a third display inside the box would show part of the game window; such a pair is refused.
            // Touching an edge is not covering.
            foreach (DisplayInfo other in displays)
            {
                if (chosen.Contains(other)) continue;
                Rect o = other.Bounds;
                if (o.Left < window.Right && window.Left < o.Right && o.Top < window.Bottom && window.Top < o.Bottom) return No(Covers(other.Name));
            }
            DisplayInfo main = chosen.First(d => d.Id == choice.MainhandId);
            DisplayInfo work2 = chosen.First(d => d.Id != choice.MainhandId);
            return new PlanResult
            {
                Ok = true, Window = window, Mode = "DUAL", MainhandId = main.Id, WorkspaceId = work2.Id,
                Mainhand = Inside(main.Bounds, window), Workspace = Inside(work2.Bounds, window),
            };
        }

        /// <summary>A display's rectangle in window pixels with the bottom-left origin WoW uses.</summary>
        private static Rect Inside(Rect d, Rect window) =>
            new Rect(d.Left - window.Left, window.Bottom - d.Bottom, d.Width, d.Height);

        private static DisplayInfo? Find(IReadOnlyList<DisplayInfo> displays, string id) =>
            displays.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
    }
}
