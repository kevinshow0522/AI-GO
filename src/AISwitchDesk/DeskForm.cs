using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal sealed partial class DeskForm : Form
	{
		internal const string AppTitle = "AI 双窗";

		private const int NormWidth = 58;

		private const int NormHeight = 292;

		private const int FoldHeight = 56;

		private const int CornerRadius = 24;

		private const int ExpandedFoldTop = NormHeight - 32;

		private static readonly int[] ButtonTop = { 21, 67, 121, 166, 211 };

		private readonly ClientWindow[] clients = new ClientWindow[2]
		{
			new ClientWindow("ChatGPT"),
			new ClientWindow("Claude")
		};

		private readonly UsageSlot[] usage = new UsageSlot[2]
		{
			new UsageSlot("ChatGPT"),
			new UsageSlot("Claude")
		};

		private readonly RailButton[] buttons = new RailButton[(int)RailAction.Fold + 1];

		private AppExitMenu exitMenu;

		private readonly ToolTip tips = new ToolTip
		{
			InitialDelay = 330,
			ReshowDelay = 90,
			AutoPopDelay = 7000,
			ShowAlways = true
		};

		private readonly Timer poll = new Timer
		{
			Interval = 240
		};

		private readonly Timer foldMotion = new Timer
		{
			Interval = 15
		};

		private readonly Stopwatch foldClock = new Stopwatch();

		private readonly Timer selectionMotion = new Timer { Interval = 15 };
		private readonly Stopwatch selectionClock = new Stopwatch();
		private float selectionStart;
		private float selectionPosition;

		private readonly RelayPump relay;

		private readonly WindowComparison comparison = new WindowComparison();

		internal readonly UploadQueue Uploads;

		private DropTargets dropTargets;

		private int dropTargetIndex;

		private readonly UsagePopup usagePopup;

		private SettingsWindow settings;

		private readonly string positionFile = Path.Combine(RelayStore.Root, "dock-position.txt");

		private readonly string thresholdFile = Path.Combine(RelayStore.Root, "quota-alert.txt");

		private readonly bool[] hotkeys = new bool[2];

		private volatile bool closing;

		private bool closeAfterQuits;

		private bool ready;

		private bool collapsed;

		private bool glass;

		private bool scaling;

		private bool resizeFromRight;

		private bool laidOut;

		private bool arranging;

		private bool gripPressed;

		private bool gripMoved;

		private Point gripOrigin;

		private Point gripLocation;

		private int threshold = 20;

		private int selected;

		private int zoom = 100;

		private int dpi = 96;

		private int scaleStart;

		private int startSpan;

		private Point scaleOrigin;

		private IntPtr observed;

		private IntPtr launchForeground;

		private DateTime nextUsageTick;

		private DateTime nextClaudeObservation;

		private Size shapeSize;

		private int shapeRadius;

		private RoundedBackdrop backdrop;

		private float expansion;

		private float foldFrom;

		private int foldStartTop;

		private int foldEndTop;

		private int foldDuration;

		internal int SurfaceTop { get; private set; }

		internal int SurfaceHeight { get; private set; }

		protected override bool ShowWithoutActivation
		{
			get
			{
				return true;
			}
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ExStyle |= 134479872;
				return createParams;
			}
		}

		internal int Zoom
		{
			get
			{
				return zoom;
			}
		}

		internal string ComparisonLabel
		{
			get
			{
				if (!comparison.Active)
				{
					return "并排对照";
				}
				return "还原窗口";
			}
		}

		private string QuotaTitle
		{
			get
			{
				if (selected != 0)
				{
					return "Claude";
				}
				return "ChatGPT · Codex";
			}
		}

		internal int Threshold
		{
			get
			{
				return threshold;
			}
		}

		private bool SettingsOpen
		{
			get
			{
				if (settings != null)
				{
					return settings.Visible;
				}
				return false;
			}
		}

		internal bool CanWork
		{
			get
			{
				if (ready && !closing && Uploads.Active == null)
				{
					return !SettingsOpen;
				}
				return false;
			}
		}

		internal bool RelayBusy
		{
			get
			{
				return relay.Busy;
			}
		}

		internal DeskForm() : this(null, null) { }

		internal DeskForm(Func<int, IntPtr> findUploadWindow, Func<int, Task<IntPtr>> openUploadWindow)
		{
			this.findUploadWindow = findUploadWindow ?? (i => clients[i].FindMainWindow());
			this.openUploadWindow = openUploadWindow ?? RequireWindow;
			Uploads = new UploadQueue(DeliverUpload, RelayStore.Root, new WindowsFormsSynchronizationContext());
			DropTrace.Write("helper", "start " + typeof(DeskForm).Assembly.GetName().Version);
			Text = "AI 双窗";
			base.Name = "AISwitchDesk";
			base.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
			base.FormBorderStyle = FormBorderStyle.None;
			base.AutoScaleMode = AutoScaleMode.None;
			base.StartPosition = FormStartPosition.Manual;
			base.TopMost = true;
			base.ShowInTaskbar = true;
			base.MaximizeBox = false;
			MinimumSize = new Size(32, 32);
			BackColor = DeskTheme.Current.RailBottom;
			tips.OwnerDraw = true;
			tips.Draw += delegate(object sender, DrawToolTipEventArgs e)
			{
				var theme = DeskTheme.Current;
				using (var fill = new SolidBrush(theme.Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
				using (var edge = new Pen(theme.Rule)) e.Graphics.DrawRectangle(edge, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
				Rectangle textBounds = Rectangle.Inflate(e.Bounds, -3, -1);
				TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font, textBounds, theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
			};
			base.KeyPreview = true;
			SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
			string[] labels = { "ChatGPT", "Claude", "并排对照", "套餐用量", "设置", "收起侧栏" };
			for (int i = 0; i < buttons.Length; i++)
			{
				RailButton railButton = new RailButton((RailAction)i, labels[i]);
				buttons[i] = railButton;
				railButton.TabIndex = i;
				base.Controls.Add(railButton);
				tips.SetToolTip(railButton, labels[i]);
			}
			buttons[0].Click += delegate
			{
				OpenApp(0);
			};
			buttons[1].Click += delegate
			{
				OpenApp(1);
			};
			for (int j = 0; j < clients.Length; j++)
			{
				int index = j;
				buttons[j].ContextRequested += delegate(Point anchor, bool fromKeyboard)
				{
					ShowExitMenu(index, anchor, fromKeyboard);
				};
				buttons[j].FilesDropped += delegate(string[] paths)
				{
					if (dropTargets != null) dropTargets.HideTargets();
					BeginInvoke((Action)delegate { QueueFiles(index, paths, false); });
				};
				buttons[j].FilesHovering += delegate { ShowDropTargets(index); };
				tips.SetToolTip(buttons[j], clients[j].Name + " · 拖到图标后台加入当前对话；拖到旁边的新建聊天区域另开聊天");
			}
			buttons[(int)RailAction.Compare].Click += delegate
			{
				ToggleComparison();
			};
			var compareButton = buttons[(int)RailAction.Compare];
			compareButton.FilesHovering += delegate { if (dropTargets != null) dropTargets.HideTargets(); };
			compareButton.FilesDropped += delegate(string[] paths)
			{
				if (dropTargets != null) dropTargets.HideTargets();
				BeginInvoke((Action)delegate { QueueFiles((int)RailAction.Compare, paths, false); });
			};
			compareButton.ContextRequested += delegate { OpenFileQueue(); };
			buttons[(int)RailAction.Usage].Click += delegate
			{
				OpenUsage();
			};
			buttons[(int)RailAction.Settings].Click += delegate
			{
				OpenSettings();
			};
			buttons[(int)RailAction.Fold].Click += delegate
			{
				Fold();
			};
			buttons[(int)RailAction.Fold].BringToFront();
			UpdateComparisonButton();
			tips.SetToolTip(this, "拖动顶部横条移动侧栏");
			usagePopup = new UsagePopup(delegate
			{
				RefreshQuota(selected);
			});
			usagePopup.Closed += delegate
			{
				buttons[(int)RailAction.Usage].Selected = false;
			};
			usage[0].Changed += UsageChanged;
			usage[1].Changed += UsageChanged;
			relay = new RelayPump(this);
			InitializeTransferFeedback();
			try
			{
				int result;
				if (File.Exists(thresholdFile) && int.TryParse(File.ReadAllText(thresholdFile), out result) && ValidThreshold(result))
				{
					threshold = result;
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			LoadPosition();
			selectionMotion.Tick += delegate
			{
				float t = RailMotion.Enabled ? Math.Min(1f, selectionClock.ElapsedMilliseconds / 180f) : 1f;
				selectionPosition = selectionStart + (selected - selectionStart) * RailMotion.SmoothStep(t);
				if (t >= 1f) selectionMotion.Stop();
				InvalidateSelection();
			};
			expansion = ((!collapsed) ? 1 : 0);
			UpdateFoldLabel();
			foldMotion.Tick += delegate
			{
				StepFold();
			};
			poll.Tick += delegate
			{
				Tick();
			};
		}

		private int P(double n)
		{
			return Math.Max(1, (int)Math.Round(n * (double)dpi / 96.0 * (double)zoom / 100.0));
		}

		private void LoadPosition()
		{
			Rectangle workingArea = Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
			Point location = new Point(workingArea.Left + 14, workingArea.Top + (workingArea.Height - NormHeight) / 2);
			try
			{
				if (File.Exists(positionFile))
				{
					string[] array = File.ReadAllText(positionFile).Trim().Split(',');
					if (array.Length == 5)
					{
						int[] array2 = Array.ConvertAll(array, int.Parse);
						Point point = new Point(array2[0], array2[1]);
						if (Screen.FromPoint(point).WorkingArea.Contains(point))
						{
							location = point;
						}
						zoom = Math.Max(80, Math.Min(160, array2[2]));
						collapsed = array2[3] == 1;
						selected = ((array2[4] == 1) ? 1 : 0);
					}
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (FormatException)
			{
			}
			catch (OverflowException)
			{
			}
			base.Bounds = new Rectangle(location, new Size(P(NormWidth), P(collapsed ? FoldHeight : NormHeight)));
		}

		private void SavePosition()
		{
			if (!ready || base.WindowState != 0)
			{
				return;
			}
			try
			{
				RelayStore.AtomicText(positionFile, string.Join(",", new int[5]
				{
					base.Left,
					base.Top,
					zoom,
					collapsed ? 1 : 0,
					selected
				}));
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		private void KeepOnScreen()
		{
			Rectangle workingArea = Screen.FromRectangle(base.Bounds).WorkingArea;
			Point point = new Point(Math.Max(workingArea.Left, Math.Min(base.Left, workingArea.Right - base.Width)), Math.Max(workingArea.Top, Math.Min(base.Top, workingArea.Bottom - base.Height)));
			if (point != base.Location)
			{
				base.Location = point;
			}
		}

		protected override void SetVisibleCore(bool value)
		{
			if (value && !ready && usagePopup != null)
			{
				IntPtr handle = base.Handle;
				dpi = Math.Max(96, (int)Native.GetDpiForWindow(handle));
				glass = Glass.Apply(handle);
				if (glass)
				{
					backdrop = RoundedBackdrop.Attach(handle, 0.1f);
				}
				RailButton[] array = buttons;
				foreach (RailButton railButton in array)
				{
					railButton.GlassEnabled = glass;
					railButton.NativeBackdrop = backdrop != null;
				}
				ready = true;
				LayoutRail();
				KeepOnScreen();
				SetSelected(selected);
			}
			base.SetVisibleCore(value);
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			poll.Start();
		}

		private void LayoutRail(int? frameTop = null)
		{
			if (!ready || laidOut)
			{
				return;
			}
			laidOut = true;
			SuspendLayout();
			try
			{
				int num = P(NormWidth);
				SurfaceHeight = P(FoldHeight + (NormHeight - FoldHeight) * expansion);
				bool flag = glass && foldMotion.Enabled;
				int num2 = (flag ? P(NormHeight) : SurfaceHeight);
				Rectangle workingArea = Screen.FromRectangle(base.Bounds).WorkingArea;
				int num3 = Math.Max(workingArea.Left, Math.Min(base.Left, workingArea.Right - num));
				int num4 = frameTop ?? base.Top;
				int val = (flag ? Math.Min(foldStartTop, foldEndTop) : num4);
				int num5 = Math.Max(workingArea.Top, Math.Min(val, workingArea.Bottom - num2));
				SurfaceTop = Math.Max(0, Math.Min(num2 - SurfaceHeight, num4 - num5));
				if (base.Left != num3 || base.Top != num5 || base.Width != num || base.Height != num2)
				{
					SetBounds(num3, num5, num, num2);
				}
				int num6 = P(CornerRadius);
				if (shapeSize != base.Size || shapeRadius != num6)
				{
					int num7 = (glass ? 1 : 0);
					using (GraphicsPath path = RailButton.Rounded(new RectangleF(-num7, -num7, base.Width + 2 * num7, base.Height + 2 * num7), num6 + num7))
					{
						Region region = base.Region;
						base.Region = new Region(path);
						if (region != null)
						{
							region.Dispose();
						}
					}
					shapeSize = base.Size;
					shapeRadius = num6;
				}
				if (backdrop != null)
				{
					backdrop.Resize(base.Width, SurfaceHeight, num6, SurfaceTop);
				}
				float foldButtonTop = 18f + (ExpandedFoldTop - 18f) * expansion;
				for (int i = 0; i < ButtonTop.Length; i++)
				{
					RailButton railButton = buttons[i];
					int num9 = ButtonTop[i];
					int num10 = num9 + 44;
					railButton.Reveal = RailMotion.SmoothStep((foldButtonTop - num10) / Math.Min(36, ExpandedFoldTop - num10));
					railButton.SetBounds(P(7.0), SurfaceTop + P(num9), P(44.0), P(43.0));
					railButton.Enabled = railButton.Reveal >= 0.75f && (railButton.Action != RailAction.Compare || !arranging);
					railButton.Visible = railButton.Reveal > 0f;
				}
				RailButton railButton2 = buttons[(int)RailAction.Fold];
				railButton2.Expansion = expansion;
				railButton2.SetBounds(P(7.0), SurfaceTop + P(foldButtonTop), P(44.0), P(29f - 7f * expansion));
			}
			finally
			{
				ResumeLayout(false);
				laidOut = false;
			}
			if (base.Visible)
			{
				Native.RedrawWindow(base.Handle, IntPtr.Zero, IntPtr.Zero, 417u);
			}
			else
			{
				Invalidate(true);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Glass.PaintRail(e.Graphics, base.ClientRectangle, -SurfaceTop, SurfaceHeight, glass, delegate(Graphics g)
			{
				g.TranslateTransform(0f, SurfaceTop);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				var theme = DeskTheme.Current;
				float line = Math.Max(1f, P(100) / 100f);
				var outline = new RectangleF(line / 2f, line / 2f, Width - line, SurfaceHeight - line);
				using (var path = RailButton.Rounded(outline, P(CornerRadius) - line / 2f))
				using (var sheen = new LinearGradientBrush(outline, Color.FromArgb(86, theme.Ink), Color.FromArgb(34, theme.Muted), 72f))
				using (var edge = new Pen(sheen, line))
					g.DrawPath(edge, path);
				float visible = RailMotion.SmoothStep((expansion - 0.65f) / 0.35f);
				if (visible > 0f)
				{
					float center = P(ButtonTop[0] + 21.5 + (ButtonTop[1] - ButtonTop[0]) * selectionPosition);
					using (var marker = new Pen(Color.FromArgb((int)(visible * 255), theme.AccentLine), P(2)))
					{
						marker.StartCap = marker.EndCap = LineCap.Round;
						g.DrawLine(marker, P(4), center - P(5), P(4), center + P(5));
					}
				}
				PaintTransferTrail(g);
				using (Pen pen = new Pen(Color.FromArgb(170, DeskTheme.Current.Muted), P(2.0)))
				{
					LineCap startCap = (pen.EndCap = LineCap.Round);
					pen.StartCap = startCap;
					g.DrawLine(pen, base.Width / 2 - P(6.0), P(11.0), base.Width / 2 + P(6.0), P(11.0));
				}
				float num = RailMotion.SmoothStep((expansion - 0.56f) / 0.18f);
				if (num > 0f)
				{
					using (Pen pen2 = new Pen(Color.FromArgb((int)(num * 70f), DeskTheme.Current.Muted), 1f))
					{
						g.DrawLine(pen2, P(16.0), P(115.0), base.Width - P(16.0), P(115.0));
					}
				}
			}, P(CornerRadius), backdrop != null);
		}

		internal void SetZoom(int value)
		{
			int num = Math.Max(80, Math.Min(160, value));
			if (num != zoom)
			{
				FinishFold();
				zoom = num;
				LayoutRail();
				KeepOnScreen();
				if (!scaling)
				{
					SavePosition();
				}
			}
		}

		private void UpdateFoldLabel()
		{
			RailButton railButton = buttons[(int)RailAction.Fold];
			string text2 = (railButton.AccessibleName = (collapsed ? "展开侧栏" : "收起侧栏"));
			railButton.Text = text2;
			tips.SetToolTip(railButton, railButton.Text);
		}

		private void Fold()
		{
			if (foldMotion.Enabled)
			{
				StepFold();
			}
			CloseExitMenu();
			HideSettings();
			usagePopup.Close();
			collapsed = !collapsed;
			UpdateFoldLabel();
			foldFrom = expansion;
			foldStartTop = base.Top + SurfaceTop;
			Rectangle workingArea = Screen.FromRectangle(base.Bounds).WorkingArea;
			int num = P(collapsed ? FoldHeight : NormHeight);
			bool flag = foldStartTop + SurfaceHeight >= workingArea.Bottom - P(2.0);
			foldEndTop = (flag ? (workingArea.Bottom - num) : Math.Min(foldStartTop, workingArea.Bottom - num));
			float num2 = Math.Abs((float)((!collapsed) ? 1 : 0) - expansion);
			if (!RailMotion.Enabled || num2 < 0.001f)
			{
				FinishFold();
				return;
			}
			foldDuration = Math.Max(80, (int)(210.0 * Math.Sqrt(num2)));
			foldClock.Restart();
			foldMotion.Start();
		}

		private void StepFold()
		{
			float num = (float)foldClock.ElapsedMilliseconds / (float)foldDuration;
			if (num >= 1f)
			{
				FinishFold();
				return;
			}
			float num2 = RailMotion.SmoothStep(num);
			expansion = foldFrom + ((float)((!collapsed) ? 1 : 0) - foldFrom) * num2;
			LayoutRail((int)Math.Round((float)foldStartTop + (float)(foldEndTop - foldStartTop) * num2));
		}

		private void FinishFold()
		{
			float num = ((!collapsed) ? 1 : 0);
			bool flag = foldMotion.Enabled || expansion != num;
			foldMotion.Stop();
			if (flag)
			{
				expansion = num;
				LayoutRail(foldEndTop);
				SavePosition();
			}
		}

		private bool RightEdge(Point p)
		{
			return p.X >= base.ClientSize.Width - P(5.0);
		}

		private bool BottomEdge(Point p)
		{
			return p.Y >= base.ClientSize.Height - P(7.0);
		}

		private bool OnGrip(Point p)
		{
			if (p.X >= P(6.0) && p.X < base.Width - P(6.0) && p.Y >= SurfaceTop)
			{
				return p.Y < SurfaceTop + P(20.0);
			}
			return false;
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left && (RightEdge(e.Location) || BottomEdge(e.Location)))
			{
				if (foldMotion.Enabled)
				{
					FinishFold();
				}
				scaling = true;
				resizeFromRight = RightEdge(e.Location) && !BottomEdge(e.Location);
				scaleStart = zoom;
				scaleOrigin = Cursor.Position;
				startSpan = (resizeFromRight ? base.Width : base.Height);
				base.Capture = true;
			}
			else if (e.Button == MouseButtons.Left && OnGrip(e.Location))
			{
				gripOrigin = Cursor.Position;
				gripLocation = base.Location;
				gripPressed = true;
				gripMoved = false;
				base.Capture = true;
			}
			else
			{
				base.OnMouseDown(e);
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			if (gripPressed)
			{
				Point position = Cursor.Position;
				int num = position.X - gripOrigin.X;
				int num2 = position.Y - gripOrigin.Y;
				if (Math.Abs(num) >= SystemInformation.DragSize.Width / 2 || Math.Abs(num2) >= SystemInformation.DragSize.Height / 2)
				{
					gripMoved = true;
				}
				if (gripMoved)
				{
					if (foldMotion.Enabled)
					{
						FinishFold();
					}
					base.Location = new Point(gripLocation.X + num, gripLocation.Y + num2);
				}
			}
			else if (scaling)
			{
				Point position2 = Cursor.Position;
				int num3 = (resizeFromRight ? (position2.X - scaleOrigin.X) : (position2.Y - scaleOrigin.Y));
				SetZoom(scaleStart + (int)Math.Round((double)(num3 * scaleStart) / (double)Math.Max(1, startSpan)));
			}
			else
			{
				Cursor = (BottomEdge(e.Location) ? Cursors.SizeNS : (RightEdge(e.Location) ? Cursors.SizeWE : Cursors.SizeAll));
				base.OnMouseMove(e);
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			if (gripPressed && e.Button == MouseButtons.Left)
			{
				gripPressed = false;
				base.Capture = false;
				if (gripMoved)
				{
					KeepOnScreen();
					SavePosition();
				}
			}
			else if (scaling && e.Button == MouseButtons.Left)
			{
				base.Capture = false;
			}
			else
			{
				base.OnMouseUp(e);
			}
		}

		protected override void OnMouseCaptureChanged(EventArgs e)
		{
			if (scaling && !base.Capture)
			{
				scaling = false;
				SavePosition();
			}
			if (gripPressed && !base.Capture)
			{
				gripPressed = false;
				KeepOnScreen();
				SavePosition();
			}
			base.OnMouseCaptureChanged(e);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == 33 && ready)
			{
				m.Result = (IntPtr)3;
				return;
			}
			if (m.Msg == 786 && ready)
			{
				int num = m.WParam.ToInt32() - 1040;
				if (num >= 0 && num < 2)
				{
					OpenApp(num);
				}
				return;
			}
			base.WndProc(ref m);
			if (!ready || closing)
			{
				return;
			}
			if (m.Msg == 132 && m.Result == (IntPtr)1 && !scaling)
			{
				Point point = PointToClient(new Point((int)m.LParam.ToInt64()));
				if (foldMotion.Enabled && (point.Y < SurfaceTop || point.Y >= SurfaceTop + SurfaceHeight))
				{
					m.Result = (IntPtr)(-1);
					return;
				}
				if (!OnGrip(point) && !RightEdge(point) && !BottomEdge(point) && GetChildAtPoint(point) == null)
				{
					m.Result = (IntPtr)2;
				}
			}
			if (m.Msg == 562)
			{
				KeepOnScreen();
				SavePosition();
			}
			if (m.Msg == 561 && foldMotion.Enabled)
			{
				FinishFold();
			}
			if (m.Msg == 736)
			{
				if (foldMotion.Enabled)
				{
					FinishFold();
				}
				dpi = Math.Max(96, (int)Native.GetDpiForWindow(base.Handle));
				LayoutRail();
				KeepOnScreen();
			}
			if (m.Msg == 126)
			{
				KeepOnScreen();
			}
		}

		private void InvalidateSelection()
		{
			Invalidate(new Rectangle(0, SurfaceTop + P(ButtonTop[0]), P(7), P(ButtonTop[1] - ButtonTop[0] + 44)));
		}

		private void SetSelected(int index)
		{
			if (index != selected && usagePopup.Visible)
			{
				usagePopup.Close();
			}
			if (index != selected)
			{
				selected = index;
				selectionStart = selectionPosition;
				if (Visible && !collapsed && RailMotion.Enabled)
				{
					selectionClock.Restart();
					selectionMotion.Start();
				}
				else
				{
					selectionMotion.Stop();
					selectionPosition = index;
					InvalidateSelection();
				}
			}
			for (int i = 0; i < 2; i++)
			{
				buttons[i].Selected = i == index;
			}
			UsageChanged();
		}

		private async void OpenApp(int index)
		{
			DropTrace.Write(clients[index].Name, "icon-click");
			if (closing)
			{
				return;
			}
			CloseExitMenu();
			HideSettings();
			if (usagePopup.Visible)
			{
				usagePopup.Close();
			}
			SetSelected(index);
			ClientWindow client = clients[index];
			if (client.Quitting)
			{
				client.OpenAfterQuit = true;
				return;
			}
			IntPtr h = client.FindMainWindow();
			if (h != IntPtr.Zero)
			{
				client.LaunchPending = false;
				if (!ClientWindow.Activate(h))
				{
					Notice(index, "窗口已打开，可从任务栏切换。");
				}
			}
			else
			{
				if (client.LaunchPending)
				{
					return;
				}
				try
				{
					await StartClient(index);
				}
				catch (Exception ex)
				{
					if (!closing)
					{
						Notice(index, ex.GetBaseException().Message);
					}
				}
			}
		}

		private async Task StartClient(int index)
		{
			ClientWindow client = clients[index];
			launchForeground = Native.GetForegroundWindow();
			client.LaunchPending = true;
			client.LaunchDeadline = DateTime.UtcNow.AddSeconds(30.0);
			try
			{
				int num = default(int);
				int num2 = num;
				int num3 = 0;
				try
				{
					client.Starting = Task.Run((Action)client.Launch);
					await client.Starting;
				}
				catch
				{
					client.LaunchPending = false;
					throw;
				}
			}
			finally
			{
				client.Starting = null;
			}
		}

		private async Task<IntPtr> RequireWindow(int index)
		{
			ClientWindow client = clients[index];
			if (client.Quitting || closing)
			{
				throw new InvalidOperationException(client.Name + " 正在退出。");
			}
			IntPtr handle2 = client.FindMainWindow();
			if (handle2 != IntPtr.Zero)
			{
				return handle2;
			}
			if (!client.LaunchPending)
			{
				await StartClient(index);
			}
			else if (client.Starting != null)
			{
				await client.Starting;
			}
			while (!closing && !client.Quitting && DateTime.UtcNow < client.LaunchDeadline)
			{
				handle2 = client.FindMainWindow();
				if (handle2 != IntPtr.Zero)
				{
					return handle2;
				}
				await Task.Delay(150);
			}
			throw new InvalidOperationException("未找到 " + client.Name + " 窗口，请打开后重试。");
		}

		private void UpdateComparisonButton()
		{
			RailButton button = buttons[(int)RailAction.Compare];
			button.Selected = comparison.Active;
			button.Text = string.Empty;
			button.AccessibleName = ComparisonLabel;
			button.Enabled = !arranging && button.Reveal >= 0.75f;
			tips.SetToolTip(button, ComparisonLabel + " · 拖入文件加入两个当前对话 · 右键查看文件队列");
			button.Invalidate();
		}

		internal async void ToggleComparison()
		{
			if (closing || arranging)
			{
				return;
			}
			arranging = true;
			UpdateComparisonButton();
			CloseExitMenu();
			HideSettings();
			usagePopup.Close();
			try
			{
				if (comparison.Active)
				{
					comparison.Restore();
					return;
				}
				Screen display = Screen.FromHandle(base.Handle);
				IntPtr[] windows = await Task.WhenAll<IntPtr>(RequireWindow(0), RequireWindow(1));
				if (!closing)
				{
					comparison.Arrange(windows[0], windows[1], display);
					ClientWindow.Activate(windows[selected]);
				}
			}
			catch (Exception ex)
			{
				Notice((int)RailAction.Compare, ex.GetBaseException().Message);
			}
			finally
			{
				arranging = false;
				if (!closing) UpdateComparisonButton();
			}
		}

		private void ShowDropTargets(int index)
		{
			if (closing) return;
			if (dropTargets == null)
			{
				dropTargets = new DropTargets();
				dropTargets.FilesDropped += delegate(bool newChat, string[] paths)
				{
					int target = dropTargetIndex;
					BeginInvoke((Action)delegate { QueueFiles(target, paths, newChat); });
				};
			}
			dropTargetIndex = index;
			dropTargets.ShowTargets(this, clients[index].Name, buttons[index].RectangleToScreen(buttons[index].ClientRectangle),
				PopupPosition(dropTargets.Size, buttons[index].Top));
		}


		private async void QuitApp(int index)
		{
			ClientWindow client = clients[index];
			if (closing || client.Quitting)
			{
				return;
			}
			bool launchPending = client.LaunchPending;
			client.Quitting = true;
			client.LaunchPending = false;
			try
			{
				Task starting = client.Starting;
				if (starting != null)
				{
					try
					{
						await starting;
					}
					catch (Win32Exception)
					{
					}
				}
				await Task.Run(delegate
				{
					client.Quit(launchPending);
				});
			}
			catch (Exception ex2)
			{
				if (!closing)
				{
					Notice(index, ex2.GetBaseException().Message);
				}
			}
			finally
			{
				client.Quitting = false;
				bool openAfterQuit = client.OpenAfterQuit;
				client.OpenAfterQuit = false;
				if (closeAfterQuits && !clients[0].Quitting && !clients[1].Quitting)
				{
					BeginInvoke(new MethodInvoker(base.Close));
				}
				else if (openAfterQuit && !closing && !closeAfterQuits)
				{
					OpenApp(index);
				}
			}
		}

		private void Notice(int index, string text)
		{
			DropTrace.Write(index < 2 ? clients[index].Name : "helper", "notice " + text);
			if (!closing)
			{
				RailButton railButton = buttons[collapsed ? (int)RailAction.Fold : index];
				tips.Show(text, railButton, railButton.Width + 5, 0, 4200);
			}
		}

		private void ShowExitMenu(int index, Point anchor, bool fromKeyboard)
		{
			if (!closing)
			{
				HideSettings();
				usagePopup.Close();
				if (exitMenu == null)
				{
					exitMenu = new AppExitMenu(this, QuitApp);
				}
				exitMenu.Show(index, anchor, !clients[index].Quitting, fromKeyboard);
			}
		}

		private void CloseExitMenu()
		{
			if (exitMenu != null)
			{
				exitMenu.Hide();
			}
		}

		private void Tick()
		{
			if (closing)
			{
				return;
			}
			IntPtr foregroundWindow = Native.GetForegroundWindow();
			if (foregroundWindow != observed)
			{
				observed = foregroundWindow;
				bool active = foregroundWindow == base.Handle || (SettingsOpen && foregroundWindow == settings.Handle);
				for (int i = 0; i < 2; i++)
				{
					if (clients[i].Recognizes(foregroundWindow))
					{
						active = true;
						if (!clients[1 - i].LaunchPending)
						{
							SetSelected(i);
						}
						break;
					}
				}
				SyncHotkeys(active);
			}
			for (int j = 0; j < 2; j++)
			{
				ClientWindow clientWindow = clients[j];
				if (!clientWindow.LaunchPending)
				{
					continue;
				}
				IntPtr intPtr = clientWindow.FindMainWindow();
				if (intPtr != IntPtr.Zero)
				{
					clientWindow.LaunchPending = false;
					if (!arranging && Uploads.Active == null && selected == j && (foregroundWindow == launchForeground || foregroundWindow == base.Handle))
					{
						ClientWindow.Activate(intPtr);
					}
				}
				else if (DateTime.UtcNow >= clientWindow.LaunchDeadline)
				{
					clientWindow.LaunchPending = false;
					if (selected == j)
					{
						Notice(j, "还没找到应用窗口，点图标可再试一次。");
					}
				}
			}
			if (DateTime.UtcNow >= nextUsageTick)
			{
				nextUsageTick = DateTime.UtcNow.AddSeconds(10.0);
				UsageChanged();
				if (selected == 0 && !collapsed && !SettingsOpen && usage[0].LastAttempt < DateTime.UtcNow.AddMinutes(-3.0) && usage[0].Report.ReadAt < DateTime.UtcNow.AddMinutes(-3.0).Ticks)
				{
					RefreshQuota(0);
				}
				if (selected == 1 && !collapsed && DateTime.UtcNow >= nextClaudeObservation && usage[1].Report.ReadAt < DateTime.UtcNow.AddMinutes(-3.0).Ticks)
				{
					nextClaudeObservation = DateTime.UtcNow.AddMinutes(1.0);
					usage[1].ObserveOpenClaude(KnownWindow("Claude"));
				}
			}
			relay.Tick();
		}

		private void SyncHotkeys(bool active)
		{
			for (int i = 0; i < 2; i++)
			{
				if (active != hotkeys[i])
				{
					if (active)
					{
						hotkeys[i] = Native.RegisterHotKey(base.Handle, 1040 + i, 16385u, (uint)(49 + i));
						continue;
					}
					Native.UnregisterHotKey(base.Handle, 1040 + i);
					hotkeys[i] = false;
				}
			}
		}

		private void UsageChanged()
		{
			if (!closing)
			{
				UsageSlot usageSlot = usage[selected];
				Allowance allowance = usageSlot.RingRow();
				double? num = ((allowance == null) ? null : new double?(allowance.Remaining));
				bool flag = UsageSlot.Low(num, threshold);
				RailButton usageButton = buttons[(int)RailAction.Usage];
				double? remaining = usageButton.Remaining;
				double? num2 = num;
				if (remaining.GetValueOrDefault() != num2.GetValueOrDefault() || remaining.HasValue != num2.HasValue || usageButton.LowAllowance != flag)
				{
					usageButton.Remaining = num;
					usageButton.LowAllowance = flag;
					usageButton.Invalidate();
				}
				string text = usageSlot.Tip((selected == 0) ? "ChatGPT · Codex" : "Claude");
				if (flag)
				{
					object obj = text;
					text = string.Concat(obj, " · 低于 ", threshold, "% 提醒线");
				}
				if (usageButton.AccessibleDescription != text)
				{
					usageButton.AccessibleDescription = text;
					tips.SetToolTip(usageButton, text);
				}
				if (usagePopup.Visible)
				{
					usagePopup.Display(QuotaTitle, usageSlot, dpi);
				}
			}
		}

		private void OpenUsage()
		{
			CloseExitMenu();
			HideSettings();
			if (usagePopup.Visible)
			{
				usagePopup.Close();
				return;
			}
			RailButton usageButton = buttons[(int)RailAction.Usage];
			usageButton.Selected = true;
			usagePopup.Display(QuotaTitle, usage[selected], dpi);
			Point screenLocation = PopupPosition(usagePopup.Size, usageButton.Top);
			usagePopup.Show(screenLocation);
			if (selected == 0 && usage[0].Report.ReadAt < DateTime.UtcNow.AddMinutes(-3.0).Ticks && usage[0].LastAttempt < DateTime.UtcNow.AddSeconds(-15.0))
			{
				RefreshQuota(0);
			}
		}

		internal Point PopupPosition(Size size, int offset)
		{
			Rectangle workingArea = Screen.FromRectangle(base.Bounds).WorkingArea;
			int num = P(8.0);
			int val = ((base.Right + num + size.Width <= workingArea.Right) ? (base.Right + num) : (base.Left - num - size.Width));
			return new Point(Math.Max(workingArea.Left, Math.Min(val, workingArea.Right - size.Width)), Math.Max(workingArea.Top, Math.Min(base.Top + offset, workingArea.Bottom - size.Height)));
		}

		internal UsageSlot Quota(int index)
		{
			return usage[index];
		}

		private static bool ValidThreshold(int value)
		{
			if (value != 0 && value != 10 && value != 20)
			{
				return value == 30;
			}
			return true;
		}

		internal bool SetTheme(DeskTheme theme)
		{
			if (theme == DeskTheme.Current) return true;
			try
			{
				theme.Save();
			}
			catch (Exception error)
			{
				if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
				DropTrace.Write("appearance", "save failed 0x" + error.HResult.ToString("X8"));
				Notice((int)RailAction.Settings, "未能保存外观设置。");
				return false;
			}
			BackColor = theme.RailBottom;
			Invalidate(true);
			if (settings != null) settings.ApplyTheme();
			if (dropTargets != null) dropTargets.ApplyTheme();
			if (exitMenu != null) exitMenu.ApplyTheme();
			usagePopup.ApplyTheme();
			if (usagePopup.Visible) usagePopup.Display(QuotaTitle, usage[selected], dpi);
			return true;
		}

		internal void SetThreshold(int value)
		{
			if (!ValidThreshold(value) || value == threshold)
			{
				return;
			}
			try
			{
				RelayStore.AtomicText(thresholdFile, value.ToString(CultureInfo.InvariantCulture));
				threshold = value;
				UsageChanged();
			}
			catch (IOException)
			{
				Notice((int)RailAction.Settings, "未能保存提醒设置。");
			}
			catch (UnauthorizedAccessException)
			{
				Notice((int)RailAction.Settings, "未能保存提醒设置。");
			}
		}

		internal Task RefreshQuota(int index)
		{
			if (index == 0)
			{
				return usage[0].Refresh(PlanUsage.OpenAI);
			}
			IntPtr target = KnownWindow("Claude");
			return usage[1].Refresh(delegate
			{
				PlanReport result = null;
				if (!ClientInput.TryUse("Claude", delegate
				{
					result = PlanUsage.Claude(target, () => new ClientWindow("Claude").FindMainWindow());
				}))
				{
					throw new IOException("Claude 正在处理附件，请稍后刷新。");
				}
				return result;
			});
		}

		private void HideSettings()
		{
			if (SettingsOpen)
			{
				settings.Hide();
			}
		}

		private void OpenSettings()
		{
			if (closing)
			{
				return;
			}
			CloseExitMenu();
			if (SettingsOpen)
			{
				settings.Hide();
				return;
			}
			if (usagePopup.Visible)
			{
				usagePopup.Close();
			}
			if (settings == null)
			{
				settings = new SettingsWindow(this);
				settings.VisibilityChanged += delegate
				{
					buttons[(int)RailAction.Settings].Selected = SettingsOpen;
					observed = IntPtr.Zero;
				};
			}
			settings.Show(base.Handle);
		}

		internal IntPtr KnownWindow(string app)
		{
			return clients[(app == "Claude") ? 1u : 0u].FindMainWindow();
		}

		internal IntPtr RelayWindow(string app, bool open)
		{
			int num = ((app == "Claude") ? 1 : 0);
			IntPtr intPtr = clients[num].FindMainWindow();
			if (clients[num].Quitting)
			{
				return IntPtr.Zero;
			}
			if (intPtr == IntPtr.Zero && open && !clients[num].LaunchPending)
			{
				OpenApp(num);
			}
			return intPtr;
		}

		internal void ExitRail()
		{
			BeginInvoke(new MethodInvoker(base.Close));
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (e.CloseReason == CloseReason.UserClosing && (clients[0].Quitting || clients[1].Quitting))
			{
				closeAfterQuits = true;
				e.Cancel = true;
				Hide();
				return;
			}
			if (foldMotion.Enabled)
			{
				FinishFold();
			}
			if (comparison.Active)
			{
				try
				{
					comparison.Restore();
				}
				catch (InvalidOperationException)
				{
				}
			}
			closing = true;
			Uploads.Stopped = true;
			Uploads.Checkpoint();
			poll.Stop();
			SyncHotkeys(false);
			if (settings != null)
			{
				settings.Dispose();
			}
			if (exitMenu != null)
			{
				exitMenu.Dispose();
			}
			usagePopup.Close();
			SavePosition();
			base.OnFormClosing(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				usage[0].Changed -= UsageChanged;
				usage[1].Changed -= UsageChanged;
				foldMotion.Dispose();
				selectionMotion.Dispose();
				transferMotion.Dispose();
				poll.Dispose();
				tips.Dispose();
				usagePopup.Dispose();
				if (dropTargets != null) dropTargets.Dispose();
				if (backdrop != null)
				{
					backdrop.Dispose();
				}
			}
			base.Dispose(disposing);
		}
	}
}
