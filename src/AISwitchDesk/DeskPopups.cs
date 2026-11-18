using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AISwitchDesk
{
    // One look for everything that pops up beside the rail. The app icons'
    // menu and the settings window are drawn by their XAML; the hover tips and
    // the rail's own menu are drawn here to the same measures: the surface
    // colour, a hairline edge, rounded corners, the same type and padding.
    internal static class PopupLook
    {
        internal const string FontName = "Microsoft YaHei UI";

        // Type size, in units of 1/96 inch, as in the XAML windows.
        internal const float FontSize = 13f;

        internal const int TipRadius = 7, TipPadX = 9, TipPadY = 5, TipMaxWidth = 420;

        internal const int MenuRadius = 12, MenuPad = 4, RowRadius = 8, RowPadX = 10, RowPadY = 7;

        internal static int Scale(double n, int dpi)
        {
            return Math.Max(1, (int)Math.Round(n * Math.Max(96, dpi) / 96.0));
        }

        // Sized in pixels for the display it is shown on, so what is measured
        // is what is drawn whichever screen the text ends up on.
        internal static Font NewFont(int dpi)
        {
            return new Font(FontName, FontSize * Math.Max(96, dpi) / 96f, GraphicsUnit.Pixel);
        }
    }

    internal sealed partial class DeskForm
    {
        private const TextFormatFlags TipFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;

        private Font tipFont;

        private int tipFontDpi;

        private IntPtr tipWindow;

        private Size tipShape;

        private Font TipFont()
        {
            if (tipFont == null || tipFontDpi != dpi)
            {
                if (tipFont != null) tipFont.Dispose();
                tipFont = PopupLook.NewFont(dpi);
                tipFontDpi = dpi;
            }
            return tipFont;
        }

        // The hover tips. Left to the system, a tip is sized for the system's
        // own type and margins, and text drawn any other way did not fit at
        // higher display scaling: "设置" showed as "设". Each tip is sized here
        // for exactly what is drawn in it.
        private void InitializeTips()
        {
            tips.OwnerDraw = true;
            tips.Popup += delegate(object sender, PopupEventArgs e)
            {
                string text = (e.AssociatedControl == null) ? null : tips.GetToolTip(e.AssociatedControl);
                if (string.IsNullOrEmpty(text)) return;
                Size size = TextRenderer.MeasureText(text, TipFont(), new Size(PopupLook.Scale(PopupLook.TipMaxWidth, dpi), 4000), TipFlags);
                e.ToolTipSize = new Size(size.Width + 2 * PopupLook.Scale(PopupLook.TipPadX, dpi), size.Height + 2 * PopupLook.Scale(PopupLook.TipPadY, dpi));
            };
            tips.Draw += delegate(object sender, DrawToolTipEventArgs e)
            {
                DeskTheme theme = DeskTheme.Current;
                Graphics g = e.Graphics;
                // The tip is a plain rectangular window. Round it once per size.
                IntPtr dc = g.GetHdc();
                IntPtr window = Native.WindowFromDC(dc);
                g.ReleaseHdc(dc);
                if (window != IntPtr.Zero && (window != tipWindow || e.Bounds.Size != tipShape))
                {
                    tipWindow = window;
                    tipShape = e.Bounds.Size;
                    Glass.RoundWindow(window, PopupLook.TipRadius);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(theme.Surface)) g.FillRectangle(fill, e.Bounds);
                using (GraphicsPath path = RailArt.Rounded(new RectangleF(.5f, .5f, e.Bounds.Width - 1f, e.Bounds.Height - 1f), PopupLook.Scale(PopupLook.TipRadius, dpi)))
                using (var edge = new Pen(theme.Rule))
                    g.DrawPath(edge, path);
                Rectangle textBounds = Rectangle.Inflate(e.Bounds, -PopupLook.Scale(PopupLook.TipPadX, dpi), -PopupLook.Scale(PopupLook.TipPadY, dpi));
                TextRenderer.DrawText(g, e.ToolTipText, TipFont(), textBounds, theme.Ink, TipFlags);
            };
        }

        // The rail's own menu, also shown by the tray icon: the same rounded
        // card and rows as the menu on the app icons.
        private void ShapeRailMenu()
        {
            if (trayMenu == null) return;
            int scale = dpi;
            if (trayMenuFont == null || trayMenuDpi != scale)
            {
                Font old = trayMenuFont;
                trayMenuFont = PopupLook.NewFont(scale);
                trayMenuDpi = scale;
                trayMenu.Font = trayMenuFont;
                foreach (ToolStripItem item in trayMenu.Items) item.Font = trayMenuFont;
                if (old != null) old.Dispose();
            }
            int pad = PopupLook.Scale(PopupLook.MenuPad, scale);
            trayMenu.Padding = new Padding(pad);
            foreach (ToolStripItem item in trayMenu.Items)
            {
                item.Margin = Padding.Empty;
                if (item is ToolStripMenuItem)
                    item.Padding = new Padding(PopupLook.Scale(PopupLook.RowPadX - 4, scale), PopupLook.Scale(PopupLook.RowPadY, scale), PopupLook.Scale(PopupLook.RowPadX + 8, scale), PopupLook.Scale(PopupLook.RowPadY, scale));
            }
            var renderer = trayMenu.Renderer as TrayMenuRenderer;
            if (renderer != null) renderer.Dpi = scale;
        }

        private void RoundRailMenu()
        {
            if (trayMenu == null || trayMenu.Width < 2 || trayMenu.Height < 2) return;
            using (GraphicsPath path = RailArt.Rounded(new RectangleF(0f, 0f, trayMenu.Width, trayMenu.Height), PopupLook.Scale(PopupLook.MenuRadius, dpi)))
            {
                Region old = trayMenu.Region;
                trayMenu.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }
    }
}
