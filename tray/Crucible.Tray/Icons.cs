using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Crucible.Tray
{
    /// <summary>The four tray icons, drawn in code: a plain coloured disc - each also with a small blue badge for "a newer
    /// tray app is on offer" (M112). No art files, nobody else's artwork. Made once and kept for the life of the program.</summary>
    internal static class Icons
    {
        private static readonly Dictionary<(TrayState, bool), Icon> Cache = new Dictionary<(TrayState, bool), Icon>();

        public static Icon For(TrayState state, bool badge = false)
        {
            if (!Cache.TryGetValue((state, badge), out Icon? icon))
            {
                icon = Draw(ColorOf(state), badge);
                Cache[(state, badge)] = icon;
            }
            return icon;
        }

        private static Color ColorOf(TrayState state)
        {
            switch (state)
            {
                case TrayState.Ok: return Color.FromArgb(46, 160, 67);
                case TrayState.Retrying: return Color.FromArgb(227, 179, 18);
                case TrayState.NeedsAttention: return Color.FromArgb(207, 34, 46);
                default: return Color.FromArgb(140, 149, 159);
            }
        }

        private static Icon Draw(Color fill, bool badge)
        {
            using (var bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(fill))
                using (var rim = new Pen(Color.FromArgb(200, 255, 255, 255), 2f))
                using (var font = new Font(FontFamily.GenericSansSerif, 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.FillEllipse(brush, 2, 2, 28, 28);
                    g.DrawEllipse(rim, 2, 2, 28, 28);
                    g.DrawString("T", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
                    if (badge)
                    {
                        // Top right, over the disc's edge, in a colour none of the four states uses.
                        using (var dot = new SolidBrush(Color.FromArgb(9, 105, 218)))
                        using (var ring = new Pen(Color.White, 2f))
                        {
                            g.FillEllipse(dot, 19, 0, 13, 13);
                            g.DrawEllipse(ring, 19, 0, 13, 13);
                        }
                    }
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }
    }
}
