using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AISwitchDesk
{
    internal sealed class DropTargets : Form
    {
        private readonly Label heading;
        private readonly Timer watch = new Timer { Interval = 120 };
        private Rectangle source;
        internal event Action<bool, string[]> FilesDropped;

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000080; return value; }
        }

        internal DropTargets()
        {
            Text = "AI 双窗 · 拖入文件";
            Name = "FileDropTargets";
            Font = new Font("Microsoft YaHei UI", 10F);
            // Scale explicitly before positioning. Automatic DPI scaling during
            // Show() used to enlarge a left-hand popup back over its source icon.
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(210, 120);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            heading = new Label { Text = "添加文件", AutoSize = true,
                Location = new Point(12, 8), UseMnemonic = false };
            Controls.Add(heading);
            Controls.Add(Zone("当前对话", "DropIntoCurrent", 35, false));
            Controls.Add(Zone("新建聊天", "DropIntoNewChat", 77, true));
            ApplyTheme();
            watch.Tick += delegate
            {
                Rectangle area = Rectangle.Union(source, Bounds);
                area.Inflate(10, 10);
                if ((Control.MouseButtons & MouseButtons.Left) == 0 || !area.Contains(Cursor.Position)) HideTargets();
            };
        }

        private sealed class DropZone : Panel
        {
            private readonly bool fresh;
            private bool hot;
            internal DropZone(bool fresh)
            {
                this.fresh = fresh;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            internal void Highlight(bool value)
            {
                if (hot == value) return;
                hot = value;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var theme = DeskTheme.Current;
                var g = e.Graphics;
                float scale = Width / 190f;
                if (scale <= 0 || Height <= 1) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float line = Math.Max(1f, scale);
                var tile = new RectangleF(line / 2, line / 2, Width - line, Height - line);
                using (var path = RailButton.Rounded(tile, 9 * scale))
                using (var fill = new SolidBrush(hot ? theme.Selected : theme.Control))
                using (var border = new Pen(hot ? theme.AccentLine : theme.Rule, line))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }
                GraphicsState saved = g.Save();
                g.TranslateTransform(12 * scale, Height / 2f);
                g.ScaleTransform(scale, scale);
                using (var pen = new Pen(hot ? theme.AccentLine : theme.Ink, 1.5f))
                using (var bubble = new GraphicsPath())
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    bubble.AddLines(new PointF[] { new PointF(3, -6), new PointF(13, -6), new PointF(15, -4),
                        new PointF(15, 3), new PointF(13, 5), new PointF(6, 5), new PointF(2, 8), new PointF(2, 5),
                        new PointF(0, 3), new PointF(0, -3) });
                    bubble.CloseFigure();
                    g.DrawPath(pen, bubble);
                    g.DrawLine(pen, 5, -0.5f, 10, -0.5f);
                    if (fresh) g.DrawLine(pen, 7.5f, -3, 7.5f, 2);
                }
                g.Restore(saved);
                int left = (int)Math.Round(39 * scale);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left - 4, Height),
                    theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
        }

        private DropZone Zone(string text, string name, int top, bool fresh)
        {
            var zone = new DropZone(fresh) { Name = name, Text = text, AccessibleName = text, AllowDrop = true,
                AccessibleRole = AccessibleRole.Pane, Location = new Point(10, top), Size = new Size(190, 33) };
            DragEventHandler feedback = delegate(object sender, DragEventArgs e)
            {
                bool valid = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop, false) &&
                    (e.AllowedEffect & DragDropEffects.Copy) != 0;
                e.Effect = valid ? DragDropEffects.Copy : DragDropEffects.None;
                zone.Highlight(valid);
            };
            zone.DragEnter += feedback;
            zone.DragOver += feedback;
            zone.DragLeave += delegate { zone.Highlight(false); };
            zone.DragDrop += delegate(object sender, DragEventArgs e)
            {
                feedback(sender, e);
                var paths = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop, false) as string[];
                bool valid = e.Effect == DragDropEffects.Copy && paths != null && paths.Length > 0;
                HideTargets();
                if (valid && FilesDropped != null) FilesDropped(fresh, paths);
            };
            return zone;
        }

        internal void ShowTargets(IWin32Window owner, string app, Rectangle icon, Point location)
        {
            source = icon;
            heading.Text = app + " · 添加文件";
            int dpi = owner == null ? 96 : (int)Native.GetDpiForWindow(owner.Handle);
            double scale = Math.Max(96,dpi) / 96.0;
            ClientSize = new Size((int)Math.Round(210*scale),(int)Math.Round(120*scale));
            heading.Location = new Point((int)Math.Round(12*scale),(int)Math.Round(8*scale));
            var current = Controls["DropIntoCurrent"];
            var fresh = Controls["DropIntoNewChat"];
            current.Bounds = new Rectangle((int)Math.Round(10*scale),(int)Math.Round(35*scale),(int)Math.Round(190*scale),(int)Math.Round(33*scale));
            fresh.Bounds = new Rectangle((int)Math.Round(10*scale),(int)Math.Round(77*scale),(int)Math.Round(190*scale),(int)Math.Round(33*scale));
            Rectangle avoid = icon;
            Native.Rect ownerBounds;
            if (owner != null && Native.GetWindowRect(owner.Handle,out ownerBounds))
                avoid = Rectangle.FromLTRB(ownerBounds.Left,ownerBounds.Top,ownerBounds.Right,ownerBounds.Bottom);
            if (!PlaceBeside(avoid,location)) { HideTargets(); return; }
            if (!Visible) Show(owner);
            // Use the realized native size as the final authority as well.
            if (!PlaceBeside(avoid,Location)) { HideTargets(); return; }
            DropTrace.Write("drop-targets","icon=" + icon + " rail=" + avoid + " popup=" + Bounds + " dpi=" + dpi);
            watch.Start();
        }

        private bool PlaceBeside(Rectangle avoid, Point requested)
        {
            Rectangle area = Screen.FromRectangle(avoid).WorkingArea;
            int gap = 8;
            int right = avoid.Right + gap;
            int left = avoid.Left - Width - gap;
            int x = right + Width <= area.Right ? right : left;
            if (x < area.Left) return false;
            Location = new Point(x,Math.Max(area.Top,Math.Min(requested.Y,area.Bottom-Height)));
            return true;
        }

        internal void ApplyTheme()
        {
            var theme = DeskTheme.Current;
            BackColor = theme.Surface;
            ForeColor = theme.Ink;
            heading.ForeColor = theme.Muted;
            foreach (Control zone in Controls) zone.BackColor = theme.Surface;
            Invalidate(true);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            using (var path = RailButton.Rounded(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 14 * ClientSize.Width / 210f))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RailButton.Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1f, ClientSize.Height - 1f), 14 * ClientSize.Width / 210f))
            using (var edge = new Pen(DeskTheme.Current.Rule)) e.Graphics.DrawPath(edge, path);
        }

        internal void HideTargets()
        {
            watch.Stop();
            foreach (Control control in Controls)
            {
                var zone = control as DropZone;
                if (zone != null) zone.Highlight(false);
            }
            Hide();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) watch.Dispose();
            base.Dispose(disposing);
        }
    }
}
