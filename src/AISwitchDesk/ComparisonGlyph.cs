using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AISwitchDesk
{
    internal static class ComparisonGlyph
    {
        internal const float RestOffset = 1.05f;
        internal const float PressedOffset = .52f;
        internal const float SlashHalfSpan = 11.8f;
        internal const float SlashWidth = 2.45f;
        internal const int SweepMilliseconds = 460;
        internal const int TapMilliseconds = 190;

        private static readonly PointF[] GptHalf = { new PointF(-16,-16), new PointF(16,-16), new PointF(-16,16) };
        private static readonly PointF[] ClaudeHalf = { new PointF(16,-16), new PointF(16,16), new PointF(-16,16) };

        internal static float Offset(bool hover, bool pressed, bool drag)
        {
            if (drag) return 2.6f;
            if (pressed) return PressedOffset;
            return hover ? 2.25f : RestOffset;
        }

        internal static void Draw(Graphics g, Action<Graphics, Color, int> drawMask, Color ink, Color slash,
            float offset, float sweep)
        {
            DrawHalf(g, drawMask, ink, GptHalf, -offset);
            DrawHalf(g, drawMask, ink, ClaudeHalf, offset);
            using (var pen = new Pen(slash, SlashWidth))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, -SlashHalfSpan, SlashHalfSpan, SlashHalfSpan, -SlashHalfSpan);
            }
            if (sweep <= 0 || sweep >= 1) return;

            float head = Ease.SmoothStep(sweep) * 1.30f;
            float from = Math.Max(0, head - .30f), to = Math.Min(1, head);
            if (from >= to) return;
            float alpha = Math.Min(1, (to - from) / .18f) * ink.A;
            float a = SlashHalfSpan * (2 * from - 1), b = SlashHalfSpan * (2 * to - 1);
            using (var halo = new Pen(Color.FromArgb((int)(alpha * .42f), Color.White), SlashWidth + 1.7f))
            using (var glint = new Pen(Color.FromArgb((int)alpha, Color.White), SlashWidth * .78f))
            {
                halo.StartCap = halo.EndCap = glint.StartCap = glint.EndCap = LineCap.Round;
                g.DrawLine(halo, a, -a, b, -b);
                g.DrawLine(glint, a, -a, b, -b);
            }
        }

        private static void DrawHalf(Graphics g, Action<Graphics, Color, int> drawMask, Color ink, PointF[] half, float offset)
        {
            var state = g.Save();
            try
            {
                g.TranslateTransform(offset, offset);
                using (var clip = new GraphicsPath())
                {
                    clip.AddPolygon(half);
                    g.SetClip(clip, CombineMode.Intersect);
                }
                drawMask(g, ink, 32);
            }
            finally { g.Restore(state); }
        }
    }
}
