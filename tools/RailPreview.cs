// Renders the rail in fixed states to PNG sheets, using the same drawing
// code as the live sidebar. Needs only System.Drawing, so it runs on Windows
// (csc) and elsewhere (Mono):
//
//   mcs -langversion:5 -r:System.Drawing.dll -out:RailPreview.exe tools/RailPreview.cs \
//       src/AISwitchDesk/{RailArt,ComparisonGlyph,DeskTheme,Ease,RailAction}.cs
//   mono RailPreview.exe src out-folder
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using AISwitchDesk;

// Stand-ins for the two things DeskTheme uses that are not System.Drawing.
namespace System.Windows
{
    public class ResourceDictionary : Dictionary<object, object> { }
}
namespace System.Windows.Media
{
    public struct Color { public byte R, G, B; public static Color FromRgb(byte r, byte g, byte b) { return new Color { R = r, G = g, B = b }; } }
    public sealed class SolidColorBrush { public SolidColorBrush(Color color) { } public void Freeze() { } }
}
namespace AISwitchDesk
{
    internal static class RelayStore
    {
        internal static string Root = Path.Combine(Path.GetTempPath(), "rail-preview-" + Guid.NewGuid().ToString("N"));
        internal static void AtomicText(string path, string body) { }
    }
}

internal sealed class Scene
{
    internal string Caption = "";
    internal float Expansion = 1f, Selection, SelectionTail = -1f;
    internal int Selected = 0, Hover = -1, Pressed = -1, Drag = -1, Focus = -1;
    internal bool Comparing, UsageOpen;
    internal double? Remaining = 62;
    internal bool Low;
    internal int TrailFrom = -1, TrailTo = -1;
    internal float Travel;
    internal TransferMark[] Feedback = new TransferMark[2];
    internal float[] FeedbackPhase = new float[2];
    internal float[] FeedbackAge = new float[2];
    internal float[] FeedbackFade = { 1f, 1f };
    internal float CutOffset = ComparisonGlyph.RestOffset, Sweep = -1f;
}

internal static class RailPreview
{
    static Bitmap[] marks;
    static float scale = 3f;

    static int P(double n) { return Math.Max(1, (int)Math.Round(n * scale)); }

    static Bitmap Load(string folder, string name)
    {
        using (var image = Image.FromFile(Path.Combine(folder, name))) return new Bitmap(image);
    }

    static void Use(string preset, string accent)
    {
        var theme = DeskTheme.Current.WithPreset(DeskTheme.Choices.First(p => p.Id == preset))
            .WithAccent(accent == null ? null : DeskTheme.Accents.First(a => a.Id == accent));
        theme.Save();
    }

    // One rail, clipped to its rounded outline, on a transparent bitmap.
    static Bitmap Rail(Scene s)
    {
        int width = P(RailArt.NormWidth);
        int height = P(RailArt.FoldHeight + (RailArt.NormHeight - RailArt.FoldHeight) * s.Expansion);
        var flat = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(flat))
        {
            RailArt.Fill(g, flat.Size, 0, height, false, false);
            var rail = new RailLook { Width = width, SurfaceHeight = height, Expansion = s.Expansion,
                SelectionPosition = s.Selection, TrailFrom = s.TrailFrom, TrailTo = s.TrailTo, TravelPhase = s.Travel, P = P };
            rail.SelectionTail = s.SelectionTail < 0f ? s.Selection : s.SelectionTail;
            RailArt.Surface(g, rail);
            float foldTop = 18f + (RailArt.NormHeight - 32 - 18f) * s.Expansion;
            for (int i = 0; i <= (int)RailAction.Fold; i++)
            {
                var look = new ButtonLook { Action = (RailAction)i, Mark = marks[i], Expansion = s.Expansion };
                Rectangle bounds;
                if (i == (int)RailAction.Fold)
                    bounds = new Rectangle(P(7), P(foldTop), P(44), P(29f - 7f * s.Expansion));
                else
                {
                    int top = RailArt.ButtonTop[i];
                    look.Reveal = Ease.SmoothStep((foldTop - (top + 44)) / Math.Min(36, RailArt.NormHeight - 32 - (top + 44)));
                    if (look.Reveal <= 0f) continue;
                    bounds = new Rectangle(P(7), P(top), P(44), P(43));
                }
                look.Width = bounds.Width;
                look.Height = bounds.Height;
                bool selected = i < 2 ? s.Selected == i : (i == (int)RailAction.Compare ? s.Comparing : i == (int)RailAction.Usage && s.UsageOpen);
                bool drag = s.Drag == i, pressed = s.Pressed == i, hover = s.Hover == i;
                look.DragOver = drag;
                look.Drop = drag ? 1f : 0f;
                look.Highlight = RailArt.TileLevel(selected, hover, pressed, drag);
                look.IconScale = RailArt.IconScale(look.Action, hover, pressed, drag);
                look.FocusCue = s.Focus == i;
                look.Remaining = s.Remaining;
                look.LowAllowance = s.Low;
                if (i < 2)
                {
                    look.Feedback = s.Feedback[i];
                    look.FeedbackPhase = s.FeedbackPhase[i];
                    look.FeedbackAge = s.FeedbackAge[i];
                    look.FeedbackFade = s.FeedbackFade[i];
                }
                if (i == (int)RailAction.Compare)
                {
                    look.CutOffset = s.Sweep >= 0f || s.CutOffset != ComparisonGlyph.RestOffset ? s.CutOffset : ComparisonGlyph.Offset(hover, pressed, drag);
                    look.Sweep = s.Sweep;
                }
                var state = g.Save();
                g.SetClip(bounds);
                g.TranslateTransform(bounds.X, bounds.Y);
                RailArt.Button(g, look);
                g.Restore(state);
            }
        }
        var shaped = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(shaped))
        using (var brush = new TextureBrush(flat))
        using (var path = RailArt.Rounded(new RectangleF(0, 0, width, height), P(RailArt.CornerRadius)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
        }
        flat.Dispose();
        return shaped;
    }

    // A row of rails over a desktop-like backdrop, each with a caption below.
    static Bitmap Row(Color desktop, Color text, params Scene[] scenes)
    {
        int cell = P(RailArt.NormWidth) + P(40), top = P(18), label = P(26);
        var sheet = new Bitmap(cell * scenes.Length, top + P(RailArt.NormHeight) + label, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(sheet))
        using (var font = new Font(FontFamily.GenericSansSerif, 6.6f * scale, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(text))
        {
            g.Clear(desktop);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            var centered = new StringFormat { Alignment = StringAlignment.Center };
            for (int i = 0; i < scenes.Length; i++)
            {
                using (var rail = Rail(scenes[i]))
                    g.DrawImage(rail, i * cell + P(20), top, rail.Width, rail.Height);
                g.DrawString(scenes[i].Caption, font, ink, new RectangleF(i * cell, top + P(RailArt.NormHeight) + P(6), cell, label), centered);
            }
        }
        return sheet;
    }

    static Bitmap Stack(params Bitmap[] rows)
    {
        var sheet = new Bitmap(rows.Max(r => r.Width), rows.Sum(r => r.Height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            int y = 0;
            foreach (var row in rows) { g.DrawImage(row, 0, y, row.Width, row.Height); y += row.Height; row.Dispose(); }
        }
        return sheet;
    }

    static Scene[] States()
    {
        return new[] {
            new Scene { Caption = "rest" },
            new Scene { Caption = "hover", Selected = 1, Selection = 1, Hover = 0 },
            new Scene { Caption = "pressed", Pressed = 3 },
            new Scene { Caption = "file over", Drag = 1 },
            new Scene { Caption = "comparing", Comparing = true, Low = true, Remaining = 12 },
            new Scene { Caption = "sending", TrailFrom = 0, TrailTo = 1, Travel = .55f,
                Feedback = new[] { TransferMark.None, TransferMark.Working }, FeedbackPhase = new[] { 0f, .55f } },
            new Scene { Caption = "delivered", Selected = 1, Selection = 1,
                Feedback = new[] { TransferMark.None, TransferMark.Success }, FeedbackPhase = new[] { 0f, 1f }, FeedbackAge = new[] { 0f, .5f } },
            new Scene { Caption = "queued / retry", Feedback = new[] { TransferMark.Waiting, TransferMark.Attention } },
            new Scene { Caption = "keyboard", Focus = 4, Remaining = null },
            new Scene { Caption = "folded", Expansion = 0f }
        };
    }

    // Frames of the three animations, as numbered PNGs for an animated preview.
    static void Motion(string output, string name, Color desktop)
    {
        string folder = Path.Combine(output, "frames-" + name);
        Directory.CreateDirectory(folder);
        const int fps = 30;
        int index = 0;
        Action<Scene> emit = delegate(Scene s)
        {
            using (var rail = Rail(s))
            using (var frame = new Bitmap(rail.Width + P(24), P(RailArt.NormHeight) + P(24), PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(desktop);
                g.DrawImage(rail, P(12), P(12), rail.Width, rail.Height);
                frame.Save(Path.Combine(folder, (index++).ToString("000") + ".png"), ImageFormat.Png);
            }
        };
        // Two trips of a file from ChatGPT to Claude, then it lands.
        for (int f = 0; f < (int)(RailArt.TravelSeconds * 2 * fps); f++)
        {
            float phase = (float)(f / (double)fps / RailArt.TravelSeconds % 1.0);
            emit(new Scene { TrailFrom = 0, TrailTo = 1, Travel = phase,
                Feedback = new[] { TransferMark.None, TransferMark.Working }, FeedbackPhase = new[] { 0f, phase } });
        }
        for (int f = 0; f < (int)(1.6 * fps); f++)
        {
            float age = f / (float)fps;
            emit(new Scene { Feedback = new[] { TransferMark.None, TransferMark.Success },
                FeedbackPhase = new[] { 0f, Math.Min(1f, age / .32f) }, FeedbackAge = new[] { 0f, age },
                FeedbackFade = new[] { 1f, Math.Min(1f, (1.6f - age) / .3f) } });
        }
        // The current-app marker travels down and back.
        for (int f = 0; f <= (int)(RailArt.MarkerSeconds * fps); f++)
        {
            float t = f / (float)(RailArt.MarkerSeconds * fps);
            emit(new Scene { Selected = 1, Selection = Ease.OutCubic(t), SelectionTail = Ease.InOutCubic(t) });
        }
        for (int f = 0; f < fps / 2; f++) emit(new Scene { Selected = 1, Selection = 1 });
        for (int f = 0; f <= (int)(RailArt.MarkerSeconds * fps); f++)
        {
            float t = f / (float)(RailArt.MarkerSeconds * fps);
            emit(new Scene { Selected = 0, Selection = 1f - Ease.OutCubic(t), SelectionTail = 1f - Ease.InOutCubic(t) });
        }
        for (int f = 0; f < fps / 2; f++) emit(new Scene());
    }

    static int Main(string[] args)
    {
        string source = args.Length > 0 ? args[0] : "src", output = args.Length > 1 ? args[1] : "preview";
        if (args.Length > 2) scale = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(output);
        marks = new[] { Load(source, "chatgpt.png"), Load(source, "claude-mark.png"), Load(source, "compare-mark.png"), null, null, Load(source, "desk-mark.png") };

        Use("codex-light", null);
        var light = Row(Color.FromArgb(226, 229, 234), Color.FromArgb(70, 76, 86), States());
        Use("github-dark", null);
        var dark = Row(Color.FromArgb(38, 42, 50), Color.FromArgb(176, 184, 196), States());
        using (var sheet = Stack(light, dark)) sheet.Save(Path.Combine(output, "states.png"), ImageFormat.Png);

        var rows = new List<Bitmap>();
        foreach (var preset in DeskTheme.Choices)
        {
            Use(preset.Id, null);
            bool darkTheme = preset.Surface.GetBrightness() < .5f;
            rows.Add(Row(darkTheme ? Color.FromArgb(38, 42, 50) : Color.FromArgb(226, 229, 234),
                darkTheme ? Color.FromArgb(176, 184, 196) : Color.FromArgb(70, 76, 86),
                new Scene { Caption = preset.Name }, new Scene { Caption = "sending", TrailFrom = 2, TrailTo = 0, Travel = .4f,
                    Feedback = new[] { TransferMark.Working, TransferMark.None }, FeedbackPhase = new[] { .4f, 0f } },
                new Scene { Caption = "file over", Drag = 2 }, new Scene { Caption = "folded", Expansion = 0f }));
        }
        using (var sheet = Stack(rows.ToArray())) sheet.Save(Path.Combine(output, "themes.png"), ImageFormat.Png);

        Use("codex-light", null);
        Motion(output, "light", Color.FromArgb(226, 229, 234));
        Use("github-dark", null);
        Motion(output, "dark", Color.FromArgb(38, 42, 50));
        Console.WriteLine("Wrote " + Path.GetFullPath(output));
        return 0;
    }
}
