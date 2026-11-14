using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AISwitchDesk
{
    internal enum TransferMark { None, Waiting, Working, Success, Attention }

    // Everything one rail button needs to be drawn. The button fills it in
    // from its own state; nothing here touches a window.
    internal sealed class ButtonLook
    {
        internal RailAction Action;
        internal Bitmap Mark;
        internal float Width = 44f, Height = 43f;
        // Three tiles can sit behind the icon, each 0..1. Select fills it with the
        // accent. Highlight is the neutral tile under the pointer. Drop marks it as
        // the place a dragged file will go.
        internal float Select, Highlight, Drop, IconScale = 1f, Reveal = 1f, Expansion = 1f;
        internal bool DragOver, FocusCue;
        internal double? Remaining;
        internal bool LowAllowance;
        internal TransferMark Feedback;
        // FeedbackPhase loops 0..1 while working and runs once 0..1 as a delivery
        // lands. FeedbackAge is seconds since it landed; FeedbackFade is 1 until
        // the badge starts to leave, then falls to 0.
        internal float FeedbackPhase, FeedbackAge, FeedbackFade = 1f;
        internal float CutOffset = ComparisonGlyph.RestOffset;
        // Progress of the comparison glyph's file sweep and tap cue, or -1 when idle.
        internal float Sweep = -1f, Tap = -1f;
    }

    // The rail behind the buttons: outline, grip, selection marker, transfer trail.
    internal sealed class RailLook
    {
        internal int Width, SurfaceHeight;
        // SelectionPosition leads and SelectionTail follows, so the marker
        // stretches while it travels. Both are 0 for ChatGPT and 1 for Claude.
        internal float Expansion = 1f, SelectionPosition, SelectionTail;
        internal int TrailFrom = -1, TrailTo = -1;
        internal float TravelPhase;
        // Design units to device pixels.
        internal Func<double, int> P;
    }

    // All rail drawing. It depends only on System.Drawing, so the same code
    // renders the live sidebar and the preview sheets in tools/RailPreview.cs.
    //
    // The look is flat: one surface colour, a hairline edge, and tiles without
    // gradients or shadows. The accent colour fills whatever is selected and
    // also marks where a file is going and where one can be dropped.
    internal static class RailArt
    {
        internal const int NormWidth = 58, NormHeight = 292, FoldHeight = 56, CornerRadius = 24;

        internal static readonly int[] ButtonTop = { 21, 67, 121, 166, 211 };

        // Seconds for one trip of the transfer trail, including its pause.
        internal const double TravelSeconds = 1.5;

        // Seconds for the current-app marker to move between the two apps.
        internal const float MarkerSeconds = .3f;

        private const float RestInk = .66f, TileRadius = 11f;

        // How strongly the neutral tile shows under the pointer.
        internal static float TileLevel(bool hover, bool pressed)
        {
            return pressed ? .9f : (hover ? .55f : 0f);
        }

        // The icon stays its own size until it is pressed or a file is over it.
        internal static float IconScale(RailAction action, bool hover, bool pressed, bool drag)
        {
            float scale = drag ? 1.06f : (pressed ? .93f : 1f);
            // The comparison glyph's halves also travel outward; leave room for their tips.
            return action == RailAction.Compare ? 1f + (scale - 1f) * .42f : scale;
        }

        private static Color Alpha(double opacity, Color color)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, (int)Math.Round(opacity * 255))), color);
        }

        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Max(.01f, Math.Min(2f * radius, Math.Min(r.Width, r.Height)));
            path.AddArc(r.Left, r.Top, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Top, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // The rail's own colour. offset and railHeight place the whole rail
        // relative to this piece, so a button continues the surface behind it.
        internal static void Fill(Graphics g, Size size, int offset, int railHeight, bool translucent, bool surfaceOnly)
        {
            using (var brush = new SolidBrush(Color.FromArgb(translucent ? 238 : 255, DeskTheme.Current.RailBottom)))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                if (surfaceOnly) g.FillRectangle(brush, 0, -offset, size.Width, railHeight);
                else g.FillRectangle(brush, new Rectangle(Point.Empty, size));
                g.CompositingMode = CompositingMode.SourceOver;
            }
        }

        internal static void Surface(Graphics g, RailLook look)
        {
            Func<double, int> P = look.P;
            DeskTheme theme = DeskTheme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float line = Math.Max(1f, P(100) / 100f);
            var outline = new RectangleF(line / 2f, line / 2f, look.Width - line, look.SurfaceHeight - line);
            using (var path = Rounded(outline, P(CornerRadius) - line / 2f))
            using (var edge = new Pen(Alpha(.13, theme.Ink), line))
                g.DrawPath(edge, path);

            // Grip.
            using (var pen = new Pen(Alpha(.26, theme.Ink), P(3.0)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, look.Width / 2f - P(6.5), P(11.0), look.Width / 2f + P(6.5), P(11.0));
            }

            // Current app. The marker is drawn between its leading and trailing
            // ends, which differ only while it is moving.
            float visible = Ease.SmoothStep((look.Expansion - 0.65f) / 0.35f);
            if (visible > 0f)
            {
                float step = ButtonTop[1] - ButtonTop[0];
                float lead = ButtonTop[0] + 21.5f + step * look.SelectionPosition;
                float tail = ButtonTop[0] + 21.5f + step * look.SelectionTail;
                float x = P(35) / 10f;
                using (var marker = new Pen(Alpha(visible, theme.AccentLine), P(3.0)))
                {
                    marker.StartCap = marker.EndCap = LineCap.Round;
                    g.DrawLine(marker, x, P(Math.Min(lead, tail) - 5.5), x, P(Math.Max(lead, tail) + 5.5));
                }
            }

            Trail(g, look);

            float rule = Ease.SmoothStep((look.Expansion - 0.56f) / 0.18f);
            if (rule > 0f)
            {
                using (var pen = new Pen(Alpha(.11 * rule, theme.Ink), line))
                    g.DrawLine(pen, P(17.0), P(115.0) + line / 2f, look.Width - P(17.0), P(115.0) + line / 2f);
            }
        }

        // A comet that leaves the sending icon, speeds up, and is drawn into the
        // receiving one. It runs in the gutter to the right of the icons.
        private static void Trail(Graphics g, RailLook look)
        {
            if (look.TrailTo < 0 || look.TrailFrom < 0 || look.Expansion < .94f) return;
            Func<double, int> P = look.P;
            Color color = DeskTheme.Current.AccentLine;
            float x = look.Width - P(40) / 10f;
            float from = P(ButtonTop[look.TrailFrom] + 21.5), to = P(ButtonTop[look.TrailTo] + 21.5);
            using (var track = new Pen(Alpha(.2, color), Math.Max(1f, P(10) / 10f)))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                g.DrawLine(track, x, from, x, to);
            }
            // The head arrives at 70% of the cycle; the tail follows and is absorbed.
            float t = look.TravelPhase / .7f;
            float head = from + (to - from) * Ease.InOutCubic(t);
            float tail = from + (to - from) * Ease.InOutCubic(t - .26f);
            float fade = Math.Min(1f, look.TravelPhase / .08f) * Math.Min(1f, (1f - look.TravelPhase) / .1f);
            if (fade <= 0f) return;
            float width = P(25) / 10f;
            if (Math.Abs(head - tail) > 1f)
            {
                float lo = Math.Min(head, tail) - 1f, hi = Math.Max(head, tail) + 1f;
                using (var blend = new LinearGradientBrush(new PointF(x, lo), new PointF(x, hi),
                    head > tail ? Alpha(0, color) : Alpha(fade, color), head > tail ? Alpha(fade, color) : Alpha(0, color)))
                using (var streak = new Pen(blend, width))
                    g.DrawLine(streak, x, tail, x, head);
            }
            using (var dot = new SolidBrush(Alpha(fade, color)))
                g.FillEllipse(dot, x - width * .7f, head - width * .7f, width * 1.4f, width * 1.4f);
        }

        internal static void Button(Graphics g, ButtonLook look)
        {
            DeskTheme theme = DeskTheme.Current;
            float select = look.Select, highlight = look.Highlight, drop = look.Drop, iconScale = look.IconScale, reveal = look.Reveal;
            float unit = look.Width / 44f;
            float height = look.Height / unit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(unit, unit);

            // The tile shrinks slightly under a press, so the press reads without colour.
            float inset = 2f + Math.Max(0f, (1f - iconScale) * 9f);
            var tile = new RectangleF(inset, inset, 44f - 2 * inset, height - 2 * inset);
            // What sits on a selected tile takes the colour that reads on the accent.
            Color line = DeskTheme.Mix(theme.AccentLine, theme.OnAccent, select);
            if (select > 0.005f || highlight > 0.005f || drop > 0.005f)
            {
                using (GraphicsPath path = Rounded(tile, TileRadius))
                {
                    using (var brush = new SolidBrush(Alpha(select * reveal, theme.Accent)))
                        g.FillPath(brush, path);
                    // Under the pointer: a neutral tile, or a slight lift on a selected one.
                    using (var brush = new SolidBrush(Alpha(.105 * highlight * (1f - drop) * reveal, DeskTheme.Mix(theme.Ink, theme.OnAccent, select))))
                        g.FillPath(brush, path);
                    if (drop > 0.005f)
                    {
                        using (var brush = new SolidBrush(Alpha(.17 * drop * (1f - select) * reveal, theme.Accent)))
                            g.FillPath(brush, path);
                        using (var ring = new Pen(Alpha(.9 * drop * reveal, line), 1.3f))
                            g.DrawPath(ring, path);
                    }
                }
            }
            Arrival(g, look, theme, height);

            // Icons rest slightly muted and come to full strength with the tile.
            float strength = RestInk + (1f - RestInk) * Math.Min(1f, Math.Max(select, Math.Max(highlight, drop)) / .5f);
            Color ink = DeskTheme.Mix(theme.Ink, theme.OnAccent, select);
            Color color = Alpha(strength * reveal, ink);
            g.TranslateTransform(22f, height / 2f - 2f * (1f - reveal));
            GraphicsState gstate = g.Save();
            g.ScaleTransform(iconScale, iconScale);
            using (Pen pen = new Pen(color, 1.8f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (look.Action)
                {
                case RailAction.ChatGPT:
                case RailAction.Claude:
                    DrawMark(g, look.Mark, color, 24);
                    break;
                case RailAction.Compare:
                    Comparison(g, look, color, Alpha(reveal, line));
                    break;
                case RailAction.Usage:
                    Allowance(g, look, color, theme);
                    break;
                case RailAction.Settings:
                    using (SolidBrush knob = new SolidBrush(color))
                    {
                        g.DrawLine(pen, -8.5f, -4.5f, 8.5f, -4.5f);
                        g.DrawLine(pen, -8.5f, 4.5f, 8.5f, 4.5f);
                        g.FillEllipse(knob, -6f, -7.3f, 5.6f, 5.6f);
                        g.FillEllipse(knob, 0.4f, 1.7f, 5.6f, 5.6f);
                    }
                    break;
                case RailAction.Fold:
                    float open = Ease.SmoothStep((look.Expansion - 0.06f) / 0.48f);
                    if (open < 1f) Brand(g, look.Mark, Alpha((1f - open) * reveal, ink), Alpha((1f - open) * reveal, line));
                    if (open > 0f)
                    {
                        g.RotateTransform(80f * (1f - open));
                        pen.Color = Alpha(open * strength, ink);
                        g.DrawLines(pen, new[] { new PointF(-4.5f, 2f), new PointF(0f, -2.2f), new PointF(4.5f, 2f) });
                    }
                    break;
                }
            }
            g.Restore(gstate);

            if (look.DragOver || drop > .5f) Plus(g, theme, drop * reveal);
            else if (look.Feedback != TransferMark.None) Badge(g, look, theme, line);

            // The keyboard ring sits just outside the tile, so it shows around a selected one too.
            if (!look.FocusCue) return;
            using (var cue = new Pen(theme.AccentLine, 1.4f))
            using (GraphicsPath path = Rounded(new RectangleF(-21.3f, -height / 2f + .7f, 42.6f, height - 1.4f), TileRadius + 1.3f))
                g.DrawPath(cue, path);
        }

        // One ring that closes in on the tile as a delivery lands.
        private static void Arrival(Graphics g, ButtonLook look, DeskTheme theme, float height)
        {
            if (look.Feedback != TransferMark.Success || look.FeedbackAge <= 0f || look.FeedbackAge >= .55f) return;
            float t = Ease.OutCubic(look.FeedbackAge / .55f);
            float inset = 2f + 5f * (1f - t);
            using (var ring = new Pen(Alpha(.75 * (1f - t) * look.Reveal, DeskTheme.Mix(theme.AccentLine, theme.OnAccent, look.Select)), 1.4f))
            using (GraphicsPath path = Rounded(new RectangleF(inset, inset, 44f - 2 * inset, height - 2 * inset), TileRadius))
                g.DrawPath(ring, path);
        }

        private static void Comparison(Graphics g, ButtonLook look, Color ink, Color slash)
        {
            float offset = look.CutOffset;
            if (look.Tap >= 0f)
                offset += (ComparisonGlyph.PressedOffset - offset) * (float)Math.Sin(Math.PI * Math.Min(1f, look.Tap));
            Bitmap mark = look.Mark;
            ComparisonGlyph.Draw(g, delegate(Graphics target, Color color, int size) { DrawMark(target, mark, color, size); },
                ink, slash, offset, look.Sweep);
        }

        // The product's own mark, shown when the rail is folded: the ChatGPT and
        // Claude halves with the accent slash between them.
        private static void Brand(Graphics g, Bitmap mark, Color ink, Color slash)
        {
            GraphicsState state = g.Save();
            g.ScaleTransform(.78f, .78f);
            ComparisonGlyph.Draw(g, delegate(Graphics target, Color color, int size) { DrawMark(target, mark, color, size); },
                ink, slash, ComparisonGlyph.RestOffset, -1f);
            g.Restore(state);
        }

        // "A file can go here."
        private static void Plus(Graphics g, DeskTheme theme, float opacity)
        {
            using (var fill = new SolidBrush(Alpha(opacity, theme.Accent)))
            using (var edge = new Pen(Alpha(opacity, theme.RailBottom), 1.2f))
            using (var pen = new Pen(Alpha(opacity, theme.OnAccent), 1.5f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.FillEllipse(fill, 7.4f, 6.4f, 11.2f, 11.2f);
                g.DrawEllipse(edge, 7.4f, 6.4f, 11.2f, 11.2f);
                g.DrawLine(pen, 10.3f, 12f, 15.7f, 12f);
                g.DrawLine(pen, 13f, 9.3f, 13f, 14.7f);
            }
        }

        // Transfer state, at the icon's lower right.
        private static void Badge(Graphics g, ButtonLook look, DeskTheme theme, Color line)
        {
            float phase = look.FeedbackPhase, fade = look.FeedbackFade * look.Reveal;
            if (fade <= 0f) return;
            GraphicsState state = g.Save();
            g.TranslateTransform(13f, 12f);
            switch (look.Feedback)
            {
            case TransferMark.Working:
                // A dot with a ring that keeps leaving it.
                float reach = Ease.OutCubic(phase);
                using (var halo = new Pen(Alpha(.55 * (1f - reach) * fade, line), 1.2f))
                    g.DrawEllipse(halo, -2.8f - 4.2f * reach, -2.8f - 4.2f * reach, 5.6f + 8.4f * reach, 5.6f + 8.4f * reach);
                using (var dot = new SolidBrush(Alpha(fade, line)))
                    g.FillEllipse(dot, -2.8f, -2.8f, 5.6f, 5.6f);
                break;
            case TransferMark.Waiting:
                using (var hole = new SolidBrush(Alpha(fade, theme.RailBottom)))
                using (var ring = new Pen(Alpha(fade, theme.AccentLine), 1.5f))
                {
                    g.FillEllipse(hole, -3.1f, -3.1f, 6.2f, 6.2f);
                    g.DrawEllipse(ring, -3.1f, -3.1f, 6.2f, 6.2f);
                }
                break;
            default:
                bool success = look.Feedback == TransferMark.Success;
                float pop = success ? .55f + .45f * Ease.OutBack(phase) : 1f;
                g.ScaleTransform(pop, pop);
                using (var fill = new SolidBrush(Alpha(fade, success ? theme.Accent : theme.Warning)))
                using (var edge = new Pen(Alpha(fade, theme.RailBottom), 1.2f))
                using (var pen = new Pen(Alpha(fade, success ? theme.OnAccent : theme.OnWarning), 1.55f))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.FillEllipse(fill, -5.8f, -5.8f, 11.6f, 11.6f);
                    g.DrawEllipse(edge, -5.8f, -5.8f, 11.6f, 11.6f);
                    if (success)
                    {
                        // The tick is drawn in two strokes, in order.
                        float first = Math.Min(1f, phase / .4f), second = Math.Max(0f, (phase - .4f) / .6f);
                        var corner = new PointF(-2.7f + 1.9f * first, .1f + 2f * first);
                        if (second <= 0f) g.DrawLine(pen, -2.7f, .1f, corner.X, corner.Y);
                        else g.DrawLines(pen, new[] { new PointF(-2.7f, .1f), corner,
                            new PointF(corner.X + 3.6f * second, corner.Y - 4.2f * second) });
                    }
                    else
                    {
                        g.DrawLine(pen, 0, -2.9f, 0, .5f);
                        g.DrawLine(pen, 0, 2.8f, 0, 2.9f);
                    }
                }
                break;
            }
            g.Restore(state);
        }

        // Paints an artwork's shape in one colour, using only its transparency.
        internal static void DrawMark(Graphics g, Bitmap mark, Color ink, int size)
        {
            if (mark == null) return;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix();
                matrix.Matrix00 = matrix.Matrix11 = matrix.Matrix22 = 0f;
                matrix.Matrix33 = ink.A / 255f;
                matrix.Matrix40 = ink.R / 255f;
                matrix.Matrix41 = ink.G / 255f;
                matrix.Matrix42 = ink.B / 255f;
                attributes.SetColorMatrix(matrix);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                int height = (int)Math.Round((double)size * mark.Height / mark.Width);
                g.DrawImage(mark, new Rectangle(-size / 2, -height / 2, size, height), 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        // Remaining allowance as a ring that empties clockwise.
        private static void Allowance(Graphics g, ButtonLook look, Color ink, DeskTheme theme)
        {
            if (look.LowAllowance)
            {
                // On a selected tile the warning needs a shade that reads on the accent.
                Color onAccent = theme.OnAccent.GetBrightness() > 0.5f ? Color.FromArgb(255, 230, 173) : Color.FromArgb(99, 62, 21);
                ink = Color.FromArgb(Math.Max((int)ink.A, (int)(230 * look.Reveal)), DeskTheme.Mix(theme.Warning, onAccent, look.Select));
            }
            RectangleF rect = new RectangleF(-9.6f, -9.6f, 19.2f, 19.2f);
            using (var track = new Pen(Color.FromArgb(ink.A * 30 / 100, ink), 2.1f))
                g.DrawEllipse(track, rect);
            if (!look.Remaining.HasValue || look.Remaining.Value <= 0.0) return;
            using (var arc = new Pen(ink, 2.1f))
            {
                arc.StartCap = arc.EndCap = LineCap.Round;
                if (look.Remaining.Value >= 100.0) g.DrawEllipse(arc, rect);
                else g.DrawArc(arc, rect, -90f, (float)(3.6 * look.Remaining.Value));
            }
        }
    }
}
