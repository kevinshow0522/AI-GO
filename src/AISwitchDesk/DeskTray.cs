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

        // Set before the form is first shown: start with the rail in the tray.
        internal bool StartInTray;

        private string TrayHintFile
        {
            get { return Path.Combine(RelayStore.Root, "tray-hint.txt"); }
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9.5f) };
            trayToggle = new ToolStripMenuItem("隐藏侧栏", null, delegate { ToggleRail(); }) { Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
            trayMenu.Items.Add(trayToggle);
            trayMenu.Items.Add(new ToolStripMenuItem("同时提问", null, delegate { ShowRail(); OpenAsk(); }));
            trayMenu.Items.Add(new ToolStripMenuItem("设置", null, delegate { ShowRail(); OpenSettings(); }));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitRail(); }));
            // The same breathing room as the sidebar's other popups.
            trayMenu.Padding = new Padding(2, 4, 2, 4);
            foreach (ToolStripItem item in trayMenu.Items)
                if (item is ToolStripMenuItem) item.Padding = new Padding(6, 4, 14, 4);
            trayMenu.Opening += delegate { trayToggle.Text = Visible ? "隐藏到托盘" : "显示侧栏"; };
            ApplyTrayTheme();
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
            trayMenu.Renderer = new TrayMenuRenderer();
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
        }

        // A flat menu in the sidebar's colours.
        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var fill = new SolidBrush(DeskTheme.Current.Surface)) e.Graphics.FillRectangle(fill, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var edge = new Pen(DeskTheme.Current.Rule))
                    e.Graphics.DrawRectangle(edge, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var area = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
                using (var path = RailArt.Rounded(area, 5f))
                using (var fill = new SolidBrush(DeskTheme.Current.Hover))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(fill, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? DeskTheme.Current.Ink : DeskTheme.Current.Muted;
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
