using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AISwitchDesk
{
    // The comparison button: half of the ChatGPT logo and half of the Claude
    // logo, both at the size of the app icons and on one centre, parted by a
    // straight cut with the accent slash lying in it.
    //
    // The cut is not baked into artwork. Each logo is drawn whole and shown
    // through one side of a line, so the line can turn: it is a diagonal while
    // the windows are apart and stands upright, ChatGPT left and Claude right,
    // while they are arranged side by side.
    internal static class ComparisonGlyph
    {
        internal const float RestOffset = 1.05f;
        internal const float PressedOffset = .52f;
        // Half the slash, measured along one axis when it lies on the diagonal.
        internal const float SlashHalfSpan = 10.2f;
        internal const float SlashWidth = 2.2f;
        internal const int SweepMilliseconds = 460;
        internal const int TapMilliseconds = 190;

        // Degrees of the cut: on the diagonal, and upright for side by side.
        internal const float RestAngle = 45f, SideBySideAngle = 90f;

        private const float LogoSize = 24f, Root2 = 1.41421356f;
        // Clear space kept on each side of the cut before the halves move apart.
        private const float HalfGap = .55f;

        internal static float Offset(bool hover, bool pressed, bool drag)
        {
            if (drag) return 2.6f;
            if (pressed) return PressedOffset;
            return hover ? 2.25f : RestOffset;
        }

        // offset is how far each half is pushed from the cut, in the same units
        // as before (along both axes on the diagonal). sweep is the progress of
        // the glint that runs along the slash while a file is held over it.
        internal static void Draw(Graphics g, Bitmap upper, Bitmap lower, Color ink, Color slash,
            float offset, float angle, float sweep)
        {
            double turn = angle * Math.PI / 180.0;
            // along runs up the slash; away points from the first logo's side to the second's.
            var along = new PointF((float)Math.Cos(turn), -(float)Math.Sin(turn));
            var away = new PointF((float)Math.Sin(turn), (float)Math.Cos(turn));
            float push = offset * Root2;
            Half(g, upper, ink, along, away, -1f, push);
            Half(g, lower, ink, along, away, 1f, push);

            // The slash lengthens a little as the halves open.
            float reach = SlashHalfSpan * Root2 + Math.Max(0f, offset - RestOffset) * .9f;
            using (var pen = new Pen(slash, SlashWidth))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, -along.X * reach, -along.Y * reach, along.X * reach, along.Y * reach);
            }
            if (sweep <= 0 || sweep >= 1) return;
            float head = Ease.SmoothStep(sweep) * 1.30f;
            float from = Math.Max(0, head - .30f), to = Math.Min(1, head);
            if (from >= to) return;
            float alpha = Math.Min(1, (to - from) / .18f) * slash.A;
            float a = reach * (2 * from - 1), b = reach * (2 * to - 1);
            using (var halo = new Pen(Color.FromArgb((int)(alpha * .42f), Color.White), SlashWidth + 1.7f))
            using (var glint = new Pen(Color.FromArgb((int)alpha, Color.White), SlashWidth * .78f))
            {
                halo.StartCap = halo.EndCap = glint.StartCap = glint.EndCap = LineCap.Round;
                g.DrawLine(halo, along.X * a, along.Y * a, along.X * b, along.Y * b);
                g.DrawLine(glint, along.X * a, along.Y * a, along.X * b, along.Y * b);
            }
        }

        // One logo, seen through its side of the cut. The side is a filled
        // polygon, so its edge along the cut is smooth at any angle.
        private static void Half(Graphics g, Bitmap mark, Color ink, PointF along, PointF away, float side, float push)
        {
            if (mark == null || ink.A == 0) return;
            // Tint the logo at the size it will appear, so thin strokes stay clean.
            float device = Math.Abs(g.Transform.Elements[0]);
            if (device <= 0f) device = 1f;
            int pixels = Math.Max(8, (int)Math.Ceiling(LogoSize * device));
            float shift = side * push;
            const float far = 20f;
            float near = side * HalfGap, edge = side * far;
            PointF[] window =
            {
                Point(along, away, far, near, shift), Point(along, away, -far, near, shift),
                Point(along, away, -far, edge, shift), Point(along, away, far, edge, shift)
            };
            using (var tinted = new Bitmap(pixels, pixels, PixelFormat.Format32bppPArgb))
            {
                using (var canvas = Graphics.FromImage(tinted))
                using (var attributes = new ImageAttributes())
                {
                    ColorMatrix matrix = new ColorMatrix();
                    matrix.Matrix00 = matrix.Matrix11 = matrix.Matrix22 = 0f;
                    matrix.Matrix33 = ink.A / 255f;
                    matrix.Matrix40 = ink.R / 255f;
                    matrix.Matrix41 = ink.G / 255f;
                    matrix.Matrix42 = ink.B / 255f;
                    attributes.SetColorMatrix(matrix);
                    canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    canvas.PixelOffsetMode = PixelOffsetMode.Half;
                    canvas.DrawImage(mark, new Rectangle(0, 0, pixels, pixels), 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attributes);
                }
                using (var brush = new TextureBrush(tinted, WrapMode.Clamp))
                {
                    brush.TranslateTransform(away.X * shift - LogoSize / 2f, away.Y * shift - LogoSize / 2f);
                    brush.ScaleTransform(LogoSize / pixels, LogoSize / pixels);
                    g.FillPolygon(brush, window);
                }
            }
        }

        private static PointF Point(PointF along, PointF away, float distanceAlong, float distanceAway, float shift)
        {
            return new PointF(along.X * distanceAlong + away.X * (distanceAway + shift),
                along.Y * distanceAlong + away.Y * (distanceAway + shift));
        }
    }
}
