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

		internal float FeedbackAge;

		internal float FeedbackFade = 1f;

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

		private float startSelect;

		private float select;

		private float targetSelect;

		private float startDrop;

		private float drop;

		private float targetDrop;

		private bool releasing;

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
				// Folded, the rail shows the product mark: the same split artwork.
				resourceName = "compare-mark.png";
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
			float nextHighlight = RailArt.TileLevel(hover, pressed);
			float nextSelect = selected ? 1f : 0f;
			float nextScale = RailArt.IconScale(Action, hover, pressed, dragOver);
			float nextDrop = dragOver ? 1f : 0f;
			float nextCut = cutEffect == null ? ComparisonGlyph.RestOffset : ComparisonGlyph.Offset(hover, pressed, dragOver);
			if (nextHighlight == target && nextScale == targetScale && nextCut == targetCutOffset && nextDrop == targetDrop && nextSelect == targetSelect) return;
			if (motion.Enabled) Animate();
			start = highlight;
			startScale = iconScale;
			startCutOffset = cutOffset;
			startDrop = drop;
			startSelect = select;
			// A released icon springs back; everything else eases to a stop.
			releasing = nextScale > iconScale && iconScale < 1f;
			target = nextHighlight;
			targetScale = nextScale;
			targetCutOffset = nextCut;
			targetDrop = nextDrop;
			targetSelect = nextSelect;
			if (Visible && RailMotion.Enabled)
			{
				duration = pressed ? 80 : (releasing ? 260 : 170);
				elapsed.Restart();
				motion.Start();
			}
			else SettleWithoutMotion();
			Invalidate();
		}

		private void SettleWithoutMotion()
		{
			highlight = target;
			select = targetSelect;
			drop = targetDrop;
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
			float weight = 1f - Ease.OutCubic(progress);
			highlight = target + (start - target) * weight;
			select = targetSelect + (startSelect - targetSelect) * weight;
			drop = targetDrop + (startDrop - targetDrop) * weight;
			iconScale = targetScale + (startScale - targetScale) * (releasing ? 1f - Ease.OutBack(progress) : weight);
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
			return RailArt.Rounded(r, radius);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			DeskForm deskForm = base.Parent as DeskForm;
			Glass.PaintRail(e.Graphics, base.ClientRectangle, base.Top - ((deskForm != null) ? deskForm.SurfaceTop : 0), (deskForm == null) ? base.Height : deskForm.SurfaceHeight, GlassEnabled, Draw, 0, NativeBackdrop);
		}

		// The drawing itself lives in RailArt. This only reports the button's state.
		private void Draw(Graphics g)
		{
			ButtonLook look = new ButtonLook();
			look.Action = Action;
			look.Mark = mark;
			look.Width = base.Width;
			look.Height = base.Height;
			look.Select = select;
			look.Highlight = highlight;
			look.Drop = drop;
			look.IconScale = iconScale;
			look.Reveal = Reveal;
			look.Expansion = Expansion;
			look.DragOver = dragOver;
			look.FocusCue = Focused && ShowFocusCues;
			look.Remaining = Remaining;
			look.LowAllowance = LowAllowance;
			look.Feedback = Feedback;
			look.FeedbackPhase = FeedbackPhase;
			look.FeedbackAge = FeedbackAge;
			look.FeedbackFade = FeedbackFade;
			look.CutOffset = cutOffset;
			if (cutEffect != null && cutEffect.IsRunning)
			{
				if (fileSweep) look.Sweep = Math.Min(1f, (float)cutEffect.ElapsedMilliseconds / ComparisonGlyph.SweepMilliseconds);
				else look.Tap = Math.Min(1f, (float)cutEffect.ElapsedMilliseconds / ComparisonGlyph.TapMilliseconds);
			}
			RailArt.Button(g, look);
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
