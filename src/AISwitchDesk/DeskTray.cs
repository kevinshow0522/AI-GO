using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AISwitchDesk
{
    // The notification-area icon. It is always there while the sidebar runs,
    // so the rail can be put away and brought back.
    internal sealed partial class DeskForm
    {
        // Posted by a second launch, to bring a hidden rail back.
        internal const int ShowRailMessage = 0x8000 + 0x5D;

        private NotifyIcon tray;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayToggle;
        private Font trayMenuFont;
        private int trayMenuDpi;

        // Set before the form is first shown: start with the rail in the tray.
        internal bool StartInTray;

        private string TrayHintFile
        {
            get { return Path.Combine(RelayStore.Root, "tray-hint.txt"); }
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip { ShowImageMargin = false };
            trayToggle = new ToolStripMenuItem("隐藏侧栏", null, delegate { ToggleRail(); });
            trayMenu.Items.Add(trayToggle);
            trayMenu.Items.Add(new ToolStripMenuItem("同时提问", null, delegate { ShowRail(); OpenAsk(); }));
            trayMenu.Items.Add(new ToolStripMenuItem("设置", null, delegate { ShowRail(); OpenSettings(); }));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitRail(); }));
            ApplyTrayTheme();
            // Type, padding and corners follow the sidebar's other popups, at
            // the scaling of the display the menu opens on. See DeskPopups.cs.
            trayMenu.Opening += delegate
            {
                trayToggle.Text = Visible ? "隐藏到托盘" : "显示侧栏";
                ShapeRailMenu();
            };
            trayMenu.Opened += delegate { RoundRailMenu(); };
            trayMenu.SizeChanged += delegate { RoundRailMenu(); };
            ShapeRailMenu();
            tray = new NotifyIcon { Icon = base.Icon, Text = AppTitle, ContextMenuStrip = trayMenu };
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleRail();
            };
            tray.Visible = true;
        }

        private void ApplyTrayTheme()
        {
            if (trayMenu == null) return;
            trayMenu.Renderer = new TrayMenuRenderer { Dpi = dpi };
            trayMenu.BackColor = DeskTheme.Current.Surface;
            trayMenu.ForeColor = DeskTheme.Current.Ink;
        }

        private void ToggleRail()
        {
            if (Visible) HideToTray();
            else ShowRail();
        }

        internal void ShowRail()
        {
            if (closing) return;
            if (!Visible)
            {
                Show();
                KeepOnScreen();
            }
        }

        internal void HideToTray()
        {
            if (closing || !Visible) return;
            if (foldMotion.Enabled) FinishFold();
            CloseExitMenu();
            HideSettings();
            HideAsk();
            if (dropTargets != null) dropTargets.HideTargets();
            Hide();
            // Say once where the rail went.
            try
            {
                if (!File.Exists(TrayHintFile))
                {
                    RelayStore.AtomicText(TrayHintFile, "shown");
                    TrayNotice("侧栏收到了托盘里。点托盘图标可以重新打开，右键有更多选项。");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void TrayNotice(string text)
        {
            if (tray != null && tray.Visible) tray.ShowBalloonTip(4000, AppTitle, text, ToolTipIcon.None);
        }

        private void ShowRailMenu(Point at)
        {
            if (closing) return;
            CloseExitMenu();
            trayMenu.Show(at);
        }

        private void DisposeTray()
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            if (trayMenu != null)
            {
                trayMenu.Dispose();
                trayMenu = null;
            }
            if (trayMenuFont != null)
            {
                trayMenuFont.Dispose();
                trayMenuFont = null;
            }
        }

        // The rail's menu as a rounded card in the sidebar's colours, with the
        // row under the pointer filled the way the app icons' menu fills it.
        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            internal int Dpi = 96;

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var fill = new SolidBrush(DeskTheme.Current.Surface)) e.Graphics.FillRectangle(fill, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RailArt.Rounded(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f), PopupLook.Scale(PopupLook.MenuRadius, Dpi)))
                using (var edge = new Pen(DeskTheme.Current.Rule))
                    e.Graphics.DrawPath(edge, path);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var area = new RectangleF(0f, 0f, e.Item.Width, e.Item.Height);
                using (var path = RailArt.Rounded(area, PopupLook.Scale(PopupLook.RowRadius, Dpi)))
                using (var fill = new SolidBrush(DeskTheme.Current.Hover))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(fill, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? DeskTheme.Current.Ink : DeskTheme.Current.Muted;
                // Centred in the row, starting at the row's own padding.
                int left = PopupLook.Scale(PopupLook.RowPadX, Dpi);
                e.TextRectangle = new Rectangle(left, 0, Math.Max(1, e.Item.Width - left), e.Item.Height);
                e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var line = new Pen(DeskTheme.Current.Rule)) e.Graphics.DrawLine(line, 8, y, e.Item.Width - 8, y);
            }
        }
    }
}
