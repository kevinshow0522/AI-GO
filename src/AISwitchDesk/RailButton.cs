using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal sealed class RailButton : Button
	{
		internal readonly RailAction Action;

		internal bool GlassEnabled;

		internal bool NativeBackdrop;

		internal float Reveal = 1f;

		internal float Expansion = 1f;

		internal double? Remaining;

		internal bool LowAllowance;

		internal TransferMark Feedback;

		internal float FeedbackPhase;

		private readonly Bitmap mark;

		private readonly Timer motion = new Timer
		{
			Interval = 15
		};

		private readonly Stopwatch elapsed = new Stopwatch();
		private readonly Stopwatch cutEffect;
		private bool fileSweep;
		private float cutOffset = ComparisonGlyph.RestOffset;
		private float startCutOffset = ComparisonGlyph.RestOffset;
		private float targetCutOffset = ComparisonGlyph.RestOffset;

		private bool selected;

		private bool hover;

		private bool pressed;

		private bool dragOver;

		private float start;

		private float highlight;

		private float target;

		private float startScale = 1f;

		private float iconScale = 1f;

		private float targetScale = 1f;

		private int duration;

		internal bool Selected
		{
			get
			{
				return selected;
			}
			set
			{
				if (selected != value)
				{
					selected = value;
					Transition();
				}
			}
		}

		internal event Action<string[]> FilesDropped;

		internal event Action FilesHovering;

		internal event Action<Point, bool> ContextRequested;

		internal RailButton(RailAction action, string label)
		{
			Action = action;
			Text = (base.AccessibleName = label);
			base.AccessibleRole = AccessibleRole.PushButton;
			base.FlatStyle = FlatStyle.Flat;
			Cursor = Cursors.Hand;
			AllowDrop = action == RailAction.ChatGPT || action == RailAction.Claude || action == RailAction.Compare;
			SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
			SetStyle(ControlStyles.DoubleBuffer | ControlStyles.OptimizedDoubleBuffer, false);
			string resourceName;
			switch (action)
			{
			default:
				resourceName = null;
				break;
			case RailAction.Fold:
				resourceName = "desk-mark.png";
				break;
			case RailAction.Claude:
				resourceName = "claude-mark.png";
				break;
			case RailAction.ChatGPT:
				resourceName = "chatgpt.png";
				break;
			case RailAction.Compare:
				resourceName = "compare-mark.png";
				cutEffect = new Stopwatch();
				break;
			}
			if (resourceName != null)
			{
				using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
				{
					using (Image original = Image.FromStream(stream))
					{
						mark = new Bitmap(original);
					}
				}
			}
			motion.Tick += delegate
			{
				Animate();
			};
		}

		private void Transition()
		{
			float nextHighlight = selected || dragOver ? 1f : (pressed ? .29f : (hover ? .16f : 0f));
			float nextScale = dragOver ? 1.085f : (pressed ? .92f : (hover ? 1.055f : 1f));
			// The split halves also travel outward; keep some room around their tips.
			if (cutEffect != null) nextScale = 1f + (nextScale - 1f) * .42f;
			float nextCut = cutEffect == null ? ComparisonGlyph.RestOffset : ComparisonGlyph.Offset(hover, pressed, dragOver);
			if (nextHighlight == target && nextScale == targetScale && nextCut == targetCutOffset) return;
			if (motion.Enabled) Animate();
			start = highlight;
			startScale = iconScale;
			startCutOffset = cutOffset;
			target = nextHighlight;
			targetScale = nextScale;
			targetCutOffset = nextCut;
			if (Visible && RailMotion.Enabled)
			{
				duration = pressed ? 70 : (dragOver ? 150 : 165);
				elapsed.Restart();
				motion.Start();
			}
			else SettleWithoutMotion();
			Invalidate();
		}

		private void SettleWithoutMotion()
		{
			highlight = target;
			iconScale = 1f;
			cutOffset = ComparisonGlyph.RestOffset;
			if (cutEffect != null) cutEffect.Reset();
			elapsed.Reset();
			motion.Stop();
		}

		private void Animate()
		{
			if (!Visible || !RailMotion.Enabled)
			{
				SettleWithoutMotion();
				Invalidate();
				return;
			}
			float progress = elapsed.IsRunning ? Math.Min(1f, (float)elapsed.ElapsedMilliseconds / duration) : 1f;
			float left = 1f - progress;
			float weight = left * left * left;
			highlight = target + (start - target) * weight;
			iconScale = targetScale + (startScale - targetScale) * weight;
			cutOffset = targetCutOffset + (startCutOffset - targetCutOffset) * weight;
			if (cutEffect != null && cutEffect.IsRunning &&
			    cutEffect.ElapsedMilliseconds >= (fileSweep ? ComparisonGlyph.SweepMilliseconds : ComparisonGlyph.TapMilliseconds))
				cutEffect.Reset();
			if (progress >= 1f && (cutEffect == null || !cutEffect.IsRunning))
			{
				elapsed.Reset();
				motion.Stop();
			}
			Invalidate();
		}

		private void StartCutEffect(bool file)
		{
			if (cutEffect == null || !Visible || !RailMotion.Enabled) return;
			fileSweep = file;
			cutEffect.Restart();
			motion.Start();
			Invalidate();
		}

		protected override void OnClick(EventArgs e)
		{
			StartCutEffect(false);
			base.OnClick(e);
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			base.OnMouseEnter(e);
			hover = true;
			Transition();
		}

		private void DragFeedback(DragEventArgs e)
		{
			bool flag = base.Enabled && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop, false) && (e.AllowedEffect & DragDropEffects.Copy) != 0;
			e.Effect = (flag ? DragDropEffects.Copy : DragDropEffects.None);
			if (dragOver != flag)
			{
				dragOver = flag;
				Transition();
				if (flag) StartCutEffect(true);
				else if (fileSweep) cutEffect.Reset();
				Invalidate();
			}
		}

		protected override void OnDragEnter(DragEventArgs e)
		{
			base.OnDragEnter(e);
			DragFeedback(e);
			DropTrace.Write(Text, "drag-enter accepted=" + dragOver + " formats=" + (e.Data == null ? "null" : string.Join(",",e.Data.GetFormats(false))));
			if (dragOver && FilesHovering != null) FilesHovering();
		}

		protected override void OnDragOver(DragEventArgs e)
		{
			base.OnDragOver(e);
			DragFeedback(e);
		}

		protected override void OnDragLeave(EventArgs e)
		{
			base.OnDragLeave(e);
			if (fileSweep) cutEffect.Reset();
			dragOver = false;
			Transition();
			Invalidate();
		}

		protected override void OnDragDrop(DragEventArgs e)
		{
			DropTrace.Write(Text, "drag-drop");
			base.OnDragDrop(e);
			DragFeedback(e);
			bool flag = dragOver;
			dragOver = false;
			Transition();
			Invalidate();
			if (flag && this.FilesDropped != null)
			{
				string[] array = e.Data.GetData(DataFormats.FileDrop, false) as string[];
				if (array != null && array.Length > 0)
				{
					this.FilesDropped(array);
				}
			}
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			hover = false;
			Transition();
		}

		protected override void OnMouseCaptureChanged(EventArgs e)
		{
			base.OnMouseCaptureChanged(e);
			if (!base.Capture && pressed)
			{
				pressed = false;
				Transition();
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Return)
			{
				pressed = true;
				Transition();
			}
			base.OnKeyDown(e);
		}

		protected override void OnKeyUp(KeyEventArgs e)
		{
			pressed = false;
			Transition();
			base.OnKeyUp(e);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == 123 && this.ContextRequested != null)
			{
				int num = (int)m.LParam.ToInt64();
				bool flag = num == -1;
				Point arg = (flag ? PointToScreen(new Point(base.ClientSize.Width / 2, base.ClientSize.Height / 2)) : new Point(num));
				this.ContextRequested(arg, flag);
				m.Result = IntPtr.Zero;
			}
			else if (m.Msg == 513 || m.Msg == 515)
			{
				if (base.Enabled)
				{
					pressed = true;
					base.Capture = true;
					Transition();
				}
				m.Result = IntPtr.Zero;
			}
			else if (m.Msg == 514 && base.Capture)
			{
				bool flag2 = base.ClientRectangle.Contains(new Point((int)m.LParam.ToInt64()));
				pressed = false;
				base.Capture = false;
				Transition();
				if (flag2 && base.Enabled)
				{
					OnClick(EventArgs.Empty);
				}
				m.Result = IntPtr.Zero;
			}
			else
			{
				base.WndProc(ref m);
			}
		}

		internal static GraphicsPath Rounded(RectangleF r, float radius)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			float num = Math.Min(2f * radius, Math.Min(r.Width, r.Height));
			graphicsPath.AddArc(r.Left, r.Top, num, num, 180f, 90f);
			graphicsPath.AddArc(r.Right - num, r.Top, num, num, 270f, 90f);
			graphicsPath.AddArc(r.Right - num, r.Bottom - num, num, num, 0f, 90f);
			graphicsPath.AddArc(r.Left, r.Bottom - num, num, num, 90f, 90f);
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			DeskForm deskForm = base.Parent as DeskForm;
			Glass.PaintRail(e.Graphics, base.ClientRectangle, base.Top - ((deskForm != null) ? deskForm.SurfaceTop : 0), (deskForm == null) ? base.Height : deskForm.SurfaceHeight, GlassEnabled, Draw, 0, NativeBackdrop);
		}

		private void Draw(Graphics g)
		{
			DeskTheme theme = DeskTheme.Current;
			float num = (float)base.Width / 44f;
			float num2 = (float)base.Height / num;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.ScaleTransform(num, num);
			if (highlight > 0.005f)
			{
				float inset = 1.5f + Math.Max(0f, (1f - iconScale) * 11f);
				using (GraphicsPath path = Rounded(new RectangleF(inset, inset, 44f - 2 * inset, num2 - 2 * inset), 12f + 3f * highlight))
				{
					using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(highlight * Reveal * 255f), theme.Accent)))
					{
						g.FillPath(brush, path);
					}
					using (var edge = new Pen(Color.FromArgb((int)(highlight * Reveal * 60f), theme.Ink), 0.8f))
						g.DrawPath(edge, path);
				}
			}
			Color baseColor = highlight >= 0.5f ? theme.OnAccent : theme.Ink;
			Color color = Color.FromArgb((int)(255f * Reveal), baseColor);
			g.TranslateTransform(22f, num2 / 2f - 2f * (1f - Reveal) - Math.Max(0f, iconScale - 1f) * 12f);
			GraphicsState gstate = g.Save();
			g.ScaleTransform(iconScale, iconScale);
			using (Pen pen = new Pen(color, 1.8f))
			{
				LineCap startCap = (pen.EndCap = LineCap.Round);
				pen.StartCap = startCap;
				pen.LineJoin = LineJoin.Round;
				switch (Action)
				{
				case RailAction.ChatGPT:
				case RailAction.Claude:
					DrawMark(g, color, 24);
					break;
				case RailAction.Compare:
					DrawComparison(g, color, theme);
					break;
				case RailAction.Usage:
					DrawAllowance(g, color);
					break;
				case RailAction.Settings:
				{
					using (SolidBrush brush2 = new SolidBrush(color))
					{
						g.DrawLine(pen, -9f, -4.5f, 9f, -4.5f);
						g.DrawLine(pen, -9f, 4.5f, 9f, 4.5f);
						g.FillEllipse(brush2, -6.2f, -7.35f, 5.7f, 5.7f);
						g.FillEllipse(brush2, 0.5f, 1.65f, 5.7f, 5.7f);
					}
					break;
				}
				case RailAction.Fold:
				{
					float num3 = RailMotion.SmoothStep((Expansion - 0.06f) / 0.48f);
					if (num3 < 1f)
					{
						DrawMark(g, Color.FromArgb((int)((1f - num3) * 255f), color), 25);
					}
					if (num3 > 0f)
					{
						g.RotateTransform(80f * (1f - num3));
						pen.Color = Color.FromArgb((int)(num3 * 204f), color);
						g.DrawLines(pen, new PointF[3]
						{
							new PointF(-4.5f, 2f),
							new PointF(0f, -2f),
							new PointF(4.5f, 2f)
						});
					}
					break;
				}
				}
			}
			g.Restore(gstate);
			if (dragOver)
			{
				using (SolidBrush brush3 = new SolidBrush(color))
				{
					using (Pen pen2 = new Pen(highlight >= 0.5f ? theme.Accent : theme.RailBottom, 1.35f))
					{
						LineCap startCap2 = (pen2.EndCap = LineCap.Round);
						pen2.StartCap = startCap2;
						Point badge = Action == RailAction.Compare ? new Point(15, 15) : new Point(13, 11);
						g.FillEllipse(brush3, badge.X - 6, badge.Y - 6, 12, 12);
						g.DrawLine(pen2, badge.X - 3, badge.Y, badge.X + 3, badge.Y);
						g.DrawLine(pen2, badge.X, badge.Y - 3, badge.X, badge.Y + 3);
					}
				}
			}
			else if (Feedback != TransferMark.None)
			{
				DrawFeedback(g, theme);
			}
			if (!Focused || !ShowFocusCues)
			{
				return;
			}
			using (Pen pen3 = new Pen(Color.FromArgb(145, color), 1f))
			{
				using (GraphicsPath path2 = Rounded(new RectangleF(-20f, (0f - num2) / 2f + 2f, 40f, num2 - 4f), 13f))
				{
					g.DrawPath(pen3, path2);
				}
			}
		}

		private void DrawComparison(Graphics g, Color ink, DeskTheme theme)
		{
			float offset = cutOffset;
			float sweep = -1f;
			Color slash = highlight >= .5f ? theme.OnAccent : theme.AccentLine;
			if (dragOver && highlight >= .5f) slash = DeskTheme.Mix(theme.Accent, theme.OnAccent, .78);
			if (cutEffect.IsRunning)
			{
				if (fileSweep)
				{
					sweep = Math.Min(1f, (float)cutEffect.ElapsedMilliseconds / ComparisonGlyph.SweepMilliseconds);
				}
				else
				{
					float t = Math.Min(1f, (float)cutEffect.ElapsedMilliseconds / ComparisonGlyph.TapMilliseconds);
					offset += (ComparisonGlyph.PressedOffset - offset) * (float)Math.Sin(Math.PI * t);
				}
			}
			ComparisonGlyph.Draw(g, DrawMark, ink, slash, offset, sweep);
		}

		private void DrawFeedback(Graphics g, DeskTheme theme)
		{
			GraphicsState state = g.Save();
			g.TranslateTransform(13, 12);
			if (Feedback == TransferMark.Success)
			{
				float scale = .82f + .18f * RailMotion.SmoothStep(FeedbackPhase);
				g.ScaleTransform(scale, scale);
			}
			bool filled = Feedback == TransferMark.Success || Feedback == TransferMark.Attention;
			Color fill = Feedback == TransferMark.Attention ? theme.Warning : (filled ? theme.Accent : theme.Surface);
			Color ink = Feedback == TransferMark.Attention ? theme.OnWarning : (filled ? theme.OnAccent : theme.AccentLine);
			using (var brush = new SolidBrush(fill))
			using (var outline = new Pen(theme.RailBottom, 1.1f))
			using (var pen = new Pen(ink, 1.55f))
			{
				pen.StartCap = pen.EndCap = LineCap.Round;
				pen.LineJoin = LineJoin.Round;
				g.FillEllipse(brush, -6.4f, -6.4f, 12.8f, 12.8f);
				g.DrawEllipse(outline, -6.4f, -6.4f, 12.8f, 12.8f);
				if (Feedback == TransferMark.Working)
					g.DrawArc(pen, -3.6f, -3.6f, 7.2f, 7.2f, FeedbackPhase * 360f, 235f);
				else if (Feedback == TransferMark.Success)
					g.DrawLines(pen, new[] { new PointF(-3f, 0), new PointF(-.9f, 2.2f),
						new PointF(-.9f + 4.1f * FeedbackPhase, 2.2f - 4.7f * FeedbackPhase) });
				else if (Feedback == TransferMark.Attention)
				{
					g.DrawLine(pen, 0, -3f, 0, .4f);
					g.DrawLine(pen, 0, 2.7f, 0, 2.9f);
				}
				else
				{
					g.DrawEllipse(pen, -3.5f, -3.5f, 7, 7);
					g.DrawLines(pen, new[] { new PointF(0,-2f), new PointF(0,0), new PointF(1.7f,1f) });
				}
			}
			g.Restore(state);
		}

		private void DrawMark(Graphics g, Color ink, int size)
		{
			using (ImageAttributes imageAttributes = new ImageAttributes())
			{
				ColorMatrix colorMatrix = new ColorMatrix();
				colorMatrix.Matrix00 = colorMatrix.Matrix11 = colorMatrix.Matrix22 = 0f;
				colorMatrix.Matrix33 = (float)(int)ink.A / 255f;
				colorMatrix.Matrix40 = (float)(int)ink.R / 255f;
				colorMatrix.Matrix41 = (float)(int)ink.G / 255f;
				colorMatrix.Matrix42 = (float)(int)ink.B / 255f;
				ColorMatrix colorMatrix2 = colorMatrix;
				imageAttributes.SetColorMatrix(colorMatrix2);
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.Half;
				int height = (int)Math.Round((double)size * mark.Height / mark.Width);
				g.DrawImage(mark, new Rectangle(-size / 2, -height / 2, size, height), 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, imageAttributes);
			}
		}

		private void DrawAllowance(Graphics g, Color ink)
		{
			if (LowAllowance)
			{
				DeskTheme theme = DeskTheme.Current;
				Color onSelection = theme.OnAccent.GetBrightness() > 0.5f ? Color.FromArgb(255, 230, 173) : Color.FromArgb(99, 62, 21);
				ink = Color.FromArgb(ink.A, highlight >= 0.5f ? onSelection : theme.Warning);
			}
			RectangleF rect = new RectangleF(-10.8f, -10.8f, 21.6f, 21.6f);
			using (Pen pen = new Pen(Color.FromArgb(ink.A * 43 / 100, ink), 1.8f))
			{
				g.DrawEllipse(pen, rect);
			}
			using (Pen pen2 = new Pen(ink, 1.9f))
			{
				LineCap startCap = (pen2.EndCap = LineCap.Round);
				pen2.StartCap = startCap;
				if (Remaining.HasValue && Remaining.Value >= 100.0)
				{
					g.DrawEllipse(pen2, rect);
				}
				else if (Remaining.HasValue && Remaining.Value > 0.0)
				{
					g.DrawArc(pen2, rect, -90f, (float)(3.6 * Remaining.Value));
				}
			}
			g.RotateTransform(45f);
			using (GraphicsPath path = Rounded(new RectangleF(-3.2f, -3.2f, 6.4f, 6.4f), 1f))
			{
				using (SolidBrush brush = new SolidBrush(Color.FromArgb(ink.A * 72 / 100, ink)))
				{
					using (Pen pen3 = new Pen(Color.FromArgb(ink.A * 72 / 100, ink), 1.25f))
					{
						if (Remaining.HasValue)
						{
							g.FillPath(brush, path);
						}
						else
						{
							g.DrawPath(pen3, path);
						}
					}
				}
			}
			g.RotateTransform(-45f);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				motion.Dispose();
				if (mark != null)
				{
					mark.Dispose();
				}
			}
			base.Dispose(disposing);
		}
	}
}
