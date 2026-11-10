using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using Media = System.Windows.Media;

namespace AISwitchDesk
{
    internal sealed class ThemePreset
    {
        internal readonly string Id, Name;
        internal readonly Color Surface, Sidebar, Control, Ink, Accent;

        internal ThemePreset(string id, string name, int surface, int sidebar, int control, int ink, int accent)
        {
            Id = id;
            Name = name;
            Surface = DeskTheme.Rgb(surface);
            Sidebar = DeskTheme.Rgb(sidebar);
            Control = DeskTheme.Rgb(control);
            Ink = DeskTheme.Rgb(ink);
            Accent = DeskTheme.Rgb(accent);
        }
    }

    internal sealed class ChatAccent
    {
        internal readonly string Id, Name;
        internal readonly Color Color;

        internal ChatAccent(string id, string name, int rgb)
        {
            Id = id;
            Name = name;
            Color = DeskTheme.Rgb(rgb);
        }
    }

    internal sealed class DeskTheme
    {
        // Paired columns in Settings: light on the left, dark on the right.
        // Chrome seeds, sidebar/widget surfaces and chat accents from GPT desktop 26.1103.4239.
        internal static readonly ThemePreset[] Choices = {
            new ThemePreset("codex-light",      "Codex",      0xFFFFFF, 0xFCFCFC, 0xFCFCFC, 0x0D0D0D, 0x0169CC),
            new ThemePreset("github-dark",      "GitHub",     0x0D1117, 0x010409, 0x161B22, 0xE6EDF3, 0x1F6FEB),
            new ThemePreset("catppuccin-latte", "Catppuccin", 0xEFF1F5, 0xE6E9EF, 0xE6E9EF, 0x4C4F69, 0x8839EF),
            new ThemePreset("dracula",          "Dracula",    0x282A36, 0x21222C, 0x343746, 0xF8F8F2, 0xFF79C6),
            new ThemePreset("solarized-light",  "Solarized",  0xFDF6E3, 0xEEE8D5, 0xEEE8D5, 0x657B83, 0xB58900),
            new ThemePreset("everforest-dark",  "Everforest", 0x2D353B, 0x2D353B, 0x2D353B, 0xD3C6AA, 0xA7C080)
        };

        internal static readonly ChatAccent[] Accents = {
            new ChatAccent("blue",   "蓝色", 0x3A83F7),
            new ChatAccent("green",  "绿色", 0x53B559),
            new ChatAccent("yellow", "黄色", 0xF6C543),
            new ChatAccent("pink",   "粉色", 0xF077AF),
            new ChatAccent("orange", "橙色", 0xEE7C37),
            new ChatAccent("purple", "紫色", 0xA67DF2)
        };

        internal static DeskTheme Current { get; private set; }

        internal readonly ThemePreset Preset;
        internal readonly ChatAccent AccentChoice;
        internal readonly string Id;
        internal readonly Color RailTop, RailBottom, Surface, Control, Ink, Muted, Accent, AccentLine, OnAccent, Warning, OnWarning;
        internal readonly Color Rule, Hover, Selected;

        private static string PreferencePath { get { return Path.Combine(RelayStore.Root, "theme.txt"); } }

        static DeskTheme()
        {
            string saved = "";
            try { if (File.Exists(PreferencePath)) saved = File.ReadAllText(PreferencePath).Trim(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string[] parts = saved.Split('|');
            string id = parts[0];
            switch (id)
            {
                case "graphite": id = "github-dark"; break;
                case "mist": id = "codex-light"; break;
                case "pine": id = "everforest-dark"; break;
                case "sand": id = "solarized-light"; break;
            }
            var preset = Choices.FirstOrDefault(p => p.Id == id) ?? Choices[0];
            var accent = parts.Length == 2 ? Accents.FirstOrDefault(a => a.Id == parts[1]) : null;
            Current = new DeskTheme(preset, accent);
        }

        private DeskTheme(ThemePreset preset, ChatAccent accent)
        {
            Preset = preset;
            AccentChoice = accent;
            Id = preset.Id + (accent == null ? "" : "|" + accent.Id);
            Surface = preset.Surface;
            RailBottom = preset.Sidebar;
            RailTop = Mix(RailBottom, Surface, .35);
            Control = preset.Control;
            Ink = preset.Ink;
            Accent = accent == null ? preset.Accent : accent.Color;
            OnAccent = TextOn(Accent);
            AccentLine = Contrasting(Accent, Surface, 3.0);
            Muted = Contrasting(Mix(Surface, Ink, .68), Surface, 4.5);
            Rule = Mix(Surface, Ink, .24);
            Hover = Mix(Control, Accent, .15);
            Selected = Mix(Control, Accent, .24);
            Warning = Contrasting(Rgb(Surface.GetBrightness() < .5f ? 0xF8D45D : 0x95611F), Surface, 3.0);
            OnWarning = TextOn(Warning);
        }

        internal DeskTheme WithPreset(ThemePreset preset)
        {
            return Preset == preset ? this : new DeskTheme(preset, AccentChoice);
        }

        internal DeskTheme WithAccent(ChatAccent accent)
        {
            return AccentChoice == accent ? this : new DeskTheme(Preset, accent);
        }

        internal void Save()
        {
            if (this == Current) return;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    RelayStore.AtomicText(PreferencePath, Id);
                    break;
                }
                catch (IOException error)
                {
                    // ReplaceFile can briefly fail while another reader holds the old file.
                    int code = error.HResult & 0xffff;
                    bool busy = code == 32 || code == 33 || code == 1175;
                    if (!busy || attempt >= 4) throw;
                    System.Threading.Thread.Sleep(16 << attempt);
                }
            }
            Current = this;
        }

        internal static Color Rgb(int value)
        {
            return Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
        }

        private static double Linear(byte value)
        {
            double s = value / 255.0;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }

        private static double Luminance(Color c)
        {
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }

        private static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }

        internal static Color TextOn(Color fill)
        {
            return Contrast(fill, Color.Black) >= Contrast(fill, Color.White) ? Color.Black : Color.White;
        }

        // Keep the original swatch and filled accents. Thin lines on light
        // backgrounds (especially yellow) use a higher-contrast shade.
        private static Color Contrasting(Color value, Color background, double minimum)
        {
            if (Contrast(value, background) >= minimum) return value;
            Color end = TextOn(background);
            for (int i = 1; i < 20; i++)
            {
                Color candidate = Mix(value, end, i / 20.0);
                if (Contrast(candidate, background) >= minimum) return candidate;
            }
            return end;
        }

        internal static Color Mix(Color from, Color to, double fraction)
        {
            return Color.FromArgb((int)Math.Round(from.R + (to.R - from.R) * fraction),
                (int)Math.Round(from.G + (to.G - from.G) * fraction),
                (int)Math.Round(from.B + (to.B - from.B) * fraction));
        }

        internal static Media.SolidColorBrush Brush(Color color)
        {
            var brush = new Media.SolidColorBrush(Media.Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        internal void ApplyResources(ResourceDictionary resources)
        {
            resources["ThemeSurface"] = Brush(Surface);
            resources["ThemeControl"] = Brush(Control);
            resources["ThemeInk"] = Brush(Ink);
            resources["ThemeMuted"] = Brush(Muted);
            resources["ThemeAccent"] = Brush(Accent);
            resources["ThemeAccentLine"] = Brush(AccentLine);
            resources["ThemeOnAccent"] = Brush(OnAccent);
            resources["ThemeRule"] = Brush(Rule);
            resources["ThemeHover"] = Brush(Hover);
            resources["ThemeSelected"] = Brush(Selected);
        }
    }
}
