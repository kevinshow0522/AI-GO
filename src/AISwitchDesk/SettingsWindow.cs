using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AISwitchDesk
{
	internal sealed class SettingsWindow : IDisposable
	{
		private readonly DeskForm desk;

		private readonly Window window;

		private readonly StackPanel gpt;

		private readonly StackPanel claude;

		private readonly ListBox list;

		private Handoff[] records = new Handoff[0];

		private bool closed;

		private bool reading;
		private bool syncingAppearance;

		private RoundedBackdrop backdrop;

		private readonly OutsideClick outsideClick;
		private readonly DependencyPropertyDescriptor comparePress =
			DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));

		private const string Muted = "ThemeMuted";
		private const string Ink = "ThemeInk";
		private bool composedGlass;

		internal bool Visible
		{
			get
			{
				return window.IsVisible;
			}
		}

		internal IntPtr Handle
		{
			get
			{
				return new WindowInteropHelper(window).Handle;
			}
		}

		internal event Action VisibilityChanged;

		internal void Hide()
		{
			if (window.IsVisible)
			{
				window.Hide();
			}
		}

		private T Control<T>(string name) where T : FrameworkElement
		{
			return (T)window.FindName(name);
		}

		private void Click(string name, RoutedEventHandler action)
		{
			Control<Button>(name).Click += action;
		}

		internal SettingsWindow(DeskForm owner)
		{
			desk = owner;
			using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.xaml"))
			{
				window = (Window)XamlReader.Load(stream);
			}
			SetUpComparisonGlyph();
			BuildThemeChoices();
			ApplyTheme();
			outsideClick = new OutsideClick((IntPtr root) => root == Handle || root == desk.Handle, Hide);
			ElementHost.EnableModelessKeyboardInterop(window);
			window.IsVisibleChanged += delegate
			{
				if (window.IsVisible && !closed)
				{
					outsideClick.Watch();
				}
				else
				{
					outsideClick.Dispose();
				}
				if (!closed && this.VisibilityChanged != null)
				{
					this.VisibilityChanged();
				}
			};
			window.Deactivated += delegate
			{
				if (!closed)
				{
					Hide();
				}
			};
			gpt = Control<StackPanel>("GptCard");
			claude = Control<StackPanel>("ClaudeCard");
			list = Control<ListBox>("LogList");
			Grid drag = Control<Grid>("DragBar");
			drag.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
			{
				if (e.OriginalSource == drag || e.OriginalSource is TextBlock)
				{
					window.DragMove();
				}
			};
			CheckBox toggle = Control<CheckBox>("AutoSend");
			toggle.IsChecked = BackgroundRelay.Enabled;
			toggle.Click += delegate
			{
				BackgroundRelay.SetEnabled(toggle.IsChecked == true);
			};
			Click("CloseTop", delegate
			{
				window.Hide();
			});
			Click("ExitRail", delegate
			{
				window.Hide();
				desk.ExitRail();
			});
			Click("Smaller", delegate
			{
				desk.SetZoom(desk.Zoom - 10);
				ScaleText();
			});
			Click("Larger", delegate
			{
				desk.SetZoom(desk.Zoom + 10);
				ScaleText();
			});
			Click("Compare", delegate
			{
				desk.ToggleComparison();
			});
			int[] array = new int[4] { 0, 10, 20, 30 };
			foreach (int num in array)
			{
				int value = num;
				RadioButton radioButton = Control<RadioButton>("Alert" + value);
				radioButton.IsChecked = value == desk.Threshold;
				radioButton.Checked += delegate
				{
					desk.SetThreshold(value);
					Control<RadioButton>("Alert" + desk.Threshold).IsChecked = true;
				};
			}
			window.PreviewKeyDown += delegate(object s, KeyEventArgs e)
			{
				if (e.Key == Key.Escape)
				{
					e.Handled = true;
					window.Hide();
				}
			};
			window.Closing += delegate(object s, CancelEventArgs e)
			{
				if (!closed)
				{
					e.Cancel = true;
					window.Dispatcher.BeginInvoke((Action)delegate
					{
						if (window.IsVisible)
						{
							window.Hide();
						}
					});
				}
			};
			Control<RadioButton>("AppearanceTab").Checked += delegate { Page("AppearancePage"); };
			Control<RadioButton>("QueueTab").Checked += delegate { Page("QueuePage"); RefreshUploads(); };
			var uploadList = Control<ItemsControl>("UploadList");
			uploadList.ItemsSource = desk.Uploads.Entries;
			uploadList.AddHandler(Button.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e)
			{
				var button = e.OriginalSource as Button;
				if (button != null) desk.Uploads.Retry(button.Tag as UploadEntry);
			}));
			Click("ClearUploads", delegate { desk.Uploads.ClearFinished(); });
			desk.Uploads.Changed += RefreshUploads;
			Control<RadioButton>("UseTab").Checked += delegate
			{
				Page("UsePage");
			};
			Control<RadioButton>("LogTab").Checked += delegate
			{
				Page("LogPage");
				Reload();
			};
			Click("Refresh", async delegate
			{
				await Refresh();
			});
			Click("OfficialPage", delegate
			{
				Process.Start(new ProcessStartInfo("claude://claude.ai/settings/usage")
				{
					UseShellExecute = true
				});
			});
			list.SelectionChanged += delegate
			{
				RecordDetail();
			};
			Click("Resume", delegate
			{
				Change(false);
			});
			Click("CancelJob", delegate
			{
				Change(true);
			});
			Click("Reload", delegate
			{
				Reload();
			});
			Click("Files", delegate
			{
				Handoff handoff = Selected();
				if (handoff != null)
				{
					OpenFolder((handoff.ReturnName != null) ? RelayStore.Folder(handoff.Id) : Path.GetDirectoryName(RelayStore.FileOf(handoff)));
				}
			});
			window.SourceInitialized += delegate
			{
				IntPtr handle = new WindowInteropHelper(window).Handle;
				HwndSource hwndSource = HwndSource.FromHwnd(handle);
				composedGlass = Glass.Apply(handle);
				if (composedGlass)
				{
					backdrop = RoundedBackdrop.Attach(handle, 0.045f);
				}
				if (hwndSource != null && hwndSource.CompositionTarget != null)
				{
					hwndSource.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
				}
				ApplyWindowBackground();
			};
			window.DpiChanged += delegate
			{
				window.Dispatcher.BeginInvoke((Action)delegate
				{
					if (!closed)
					{
						RoundPanel();
					}
				});
			};
			desk.Quota(0).Changed += OnQuotaChange;
			desk.Quota(1).Changed += OnQuotaChange;
		}

		internal void ApplyTheme()
		{
			DeskTheme.Current.ApplyResources(window.Resources);
			ApplyWindowBackground();
			SyncAppearance();
		}

		internal void ShowFileQueue()
		{
			Control<RadioButton>("QueueTab").IsChecked = true;
			RefreshUploads();
		}

		private void RefreshUploads()
		{
			if (closed || Control<RadioButton>("QueueTab").IsChecked != true) return;
			var rows = desk.Uploads.Entries;
			int done = rows.Count(r => r.State == UploadState.Added);
			int failed = rows.Count(r => r.CanRetry);
			Control<TextBlock>("UploadSummary").Text = rows.Count == 0 ? "暂无文件" :
				"已加入 " + done + "/" + rows.Count + (failed == 0 ? "" : " · " + failed + " 项待重试");
			Control<Button>("ClearUploads").IsEnabled = done + failed > 0;
		}

		private void ApplyWindowBackground()
		{
			if (Handle == IntPtr.Zero) return;
			var source = HwndSource.FromHwnd(Handle);
			var surface = (SolidColorBrush)window.Resources["ThemeSurface"];
			if (source != null && source.CompositionTarget != null)
				source.CompositionTarget.BackgroundColor = composedGlass ? Colors.Transparent : surface.Color;
			window.Background = composedGlass ? System.Windows.Media.Brushes.Transparent : surface;
		}

		private void BuildThemeChoices()
		{
			var panel = Control<UniformGrid>("ThemeChoices");
			foreach (ThemePreset choice in DeskTheme.Choices)
			{
				var content = new StackPanel { Orientation = Orientation.Horizontal };
				var sample = new Grid { Width = 37, Height = 25, Margin = new Thickness(0, 0, 9, 0) };
				var rule = DeskTheme.Brush(DeskTheme.Mix(choice.Surface, choice.Ink, .30));
				sample.Children.Add(new Border { Background = DeskTheme.Brush(choice.Surface), BorderBrush = rule,
					BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) });
				sample.Children.Add(new Border { Width = 8, Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
					Background = DeskTheme.Brush(choice.Sidebar), CornerRadius = new CornerRadius(3) });
				sample.Children.Add(new Border { Width = 4, Height = 4, Margin = new Thickness(5, 6, 0, 0),
					HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
					Background = DeskTheme.Brush(choice.Accent), CornerRadius = new CornerRadius(2) });
				sample.Children.Add(new Border { Height = 3, Margin = new Thickness(14, 8, 5, 0), VerticalAlignment = VerticalAlignment.Top,
					Background = DeskTheme.Brush(choice.Accent), CornerRadius = new CornerRadius(2) });
				sample.Children.Add(new Border { Height = 2, Margin = new Thickness(14, 15, 10, 0), VerticalAlignment = VerticalAlignment.Top,
					Background = rule, CornerRadius = new CornerRadius(1) });
				content.Children.Add(sample);
				content.Children.Add(Text(choice.Name, 13, Ink, true));
				var button = new RadioButton { Content = content, Tag = choice, GroupName = "DeskSkins",
					Style = (Style)window.Resources["SkinChoice"] };
				AutomationProperties.SetName(button, choice.Name + (choice.Surface.GetBrightness() > .5f ? " 浅色主题" : " 深色主题"));
				panel.Children.Add(button);
				button.Checked += delegate { ChooseAppearance(DeskTheme.Current.WithPreset(choice)); };
			}
			var accents = Control<UniformGrid>("AccentChoices");
			foreach (ChatAccent choice in DeskTheme.Accents)
			{
				var button = new RadioButton { GroupName = "DeskAccents", Content = choice.Name, Tag = choice,
					Background = DeskTheme.Brush(choice.Color), BorderBrush = DeskTheme.Brush(DeskTheme.TextOn(choice.Color)),
					Style = (Style)window.Resources["AccentChoice"] };
				AutomationProperties.SetName(button, choice.Name + "强调色");
				accents.Children.Add(button);
				button.Checked += delegate { ChooseAppearance(DeskTheme.Current.WithAccent(choice)); };
			}
			Click("OriginalAccent", delegate { ChooseAppearance(DeskTheme.Current.WithAccent(null)); });
		}

		private void ChooseAppearance(DeskTheme theme)
		{
			if (!syncingAppearance && !desk.SetTheme(theme)) SyncAppearance();
		}

		private void SyncAppearance()
		{
			var theme = DeskTheme.Current;
			syncingAppearance = true;
			try
			{
				foreach (RadioButton item in Control<UniformGrid>("ThemeChoices").Children)
					item.IsChecked = item.Tag == theme.Preset;
				foreach (RadioButton item in Control<UniformGrid>("AccentChoices").Children)
					item.IsChecked = item.Tag == theme.AccentChoice;
				Control<TextBlock>("AccentValue").Text = theme.AccentChoice == null ? "主题原色" : theme.AccentChoice.Name;
				Control<Button>("OriginalAccent").IsEnabled = theme.AccentChoice != null;
			}
			finally { syncingAppearance = false; }
		}

		internal void Show(IntPtr owner)
		{
			WindowInteropHelper windowInteropHelper = new WindowInteropHelper(window);
			if (windowInteropHelper.Owner != owner)
			{
				windowInteropHelper.Owner = owner;
			}
			IntPtr intPtr = windowInteropHelper.EnsureHandle();
			double num = (double)Native.GetDpiForWindow(owner) / 96.0;
			System.Drawing.Size size = new System.Drawing.Size((int)Math.Round(window.Width * num), (int)Math.Round(window.Height * num));
			System.Drawing.Point point = desk.PopupPosition(size, 0);
			Native.SetWindowPos(intPtr, IntPtr.Zero, point.X, point.Y, size.Width, size.Height, 20u);
			RoundPanel();
			ScaleText();
			PaintQuota();
			Control<TextBlock>("CompareLabel").Text = desk.ComparisonLabel;
			Control<CheckBox>("AutoSend").IsChecked = BackgroundRelay.Enabled;
			if (Control<RadioButton>("LogTab").IsChecked == true)
			{
				Reload();
			}
			window.Show();
		}

		private void RoundPanel()
		{
			IntPtr handle = Handle;
			double topLeft = Control<Border>("Surface").CornerRadius.TopLeft;
			Glass.RoundWindow(handle, topLeft);
			Native.Rect rect;
			if (backdrop != null && Native.GetWindowRect(handle, out rect))
			{
				backdrop.Resize(rect.Width, rect.Height, (float)(topLeft * (double)Native.GetDpiForWindow(handle) / 96.0));
			}
		}

		private void ScaleText()
		{
			Control<TextBlock>("ScaleValue").Text = desk.Zoom + "%";
			Control<Button>("Smaller").IsEnabled = desk.Zoom > 80;
			Control<Button>("Larger").IsEnabled = desk.Zoom < 160;
		}

		private void OnQuotaChange()
		{
			if (window.IsVisible && !closed)
			{
				PaintQuota();
			}
		}

		private void PaintQuota()
		{
			Display(gpt, "ChatGPT · Codex", desk.Quota(0));
			Display(claude, "Claude", desk.Quota(1));
		}

		private void SetUpComparisonGlyph()
		{
			using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("compare-mark.png"))
			{
				var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
				var mask = new ImageBrush(image) { Stretch = Stretch.Uniform };
				mask.Freeze();
				Control<Border>("CompareUpper").OpacityMask = Control<Border>("CompareLower").OpacityMask = mask;
			}
			var slash = Control<System.Windows.Shapes.Line>("CompareSlash");
			slash.X1 = slash.Y2 = 18 - ComparisonGlyph.SlashHalfSpan;
			slash.Y1 = slash.X2 = 18 + ComparisonGlyph.SlashHalfSpan;
			slash.StrokeThickness = ComparisonGlyph.SlashWidth;
			var button = Control<Button>("Compare");
			button.MouseEnter += AdjustComparisonGlyph;
			button.MouseLeave += AdjustComparisonGlyph;
			button.IsVisibleChanged += delegate { AdjustComparisonGlyph(button, EventArgs.Empty); };
			comparePress.AddValueChanged(button, AdjustComparisonGlyph);
			AdjustComparisonGlyph(button, EventArgs.Empty);
		}

		private void AdjustComparisonGlyph(object sender, EventArgs e)
		{
			var button = (Button)sender;
			bool animate = button.IsVisible && RailMotion.Enabled;
			double offset = animate ? ComparisonGlyph.Offset(button.IsMouseOver, button.IsPressed, false) : ComparisonGlyph.RestOffset;
			int duration = button.IsPressed ? 70 : 165;
			MoveComparisonHalf((TranslateTransform)Control<Border>("CompareUpper").RenderTransform, -offset, animate, duration);
			MoveComparisonHalf((TranslateTransform)Control<Border>("CompareLower").RenderTransform, offset, animate, duration);
		}

		private static void MoveComparisonHalf(TranslateTransform half, double offset, bool animate, int duration)
		{
			double from = half.X;
			half.BeginAnimation(TranslateTransform.XProperty, null);
			half.BeginAnimation(TranslateTransform.YProperty, null);
			half.X = half.Y = offset;
			if (!animate || Math.Abs(from - offset) < .01) return;
			var slide = new DoubleAnimation(from, offset, TimeSpan.FromMilliseconds(duration)) {
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
			};
			half.BeginAnimation(TranslateTransform.XProperty, slide);
			half.BeginAnimation(TranslateTransform.YProperty, slide);
		}

		public void Dispose()
		{
			closed = true;
			desk.Uploads.Changed -= RefreshUploads;
			comparePress.RemoveValueChanged(Control<Button>("Compare"), AdjustComparisonGlyph);
			outsideClick.Dispose();
			desk.Quota(0).Changed -= OnQuotaChange;
			desk.Quota(1).Changed -= OnQuotaChange;
			if (backdrop != null)
			{
				backdrop.Dispose();
			}
			window.Close();
		}

		private void Page(string name)
		{
			string[] array = { "AppearancePage", "UsePage", "LogPage", "QueuePage" };
			foreach (string text in array)
			{
				Control<Grid>(text).Visibility = ((!(text == name)) ? Visibility.Collapsed : Visibility.Visible);
			}
		}

		private static void OpenFolder(string path)
		{
			Process.Start("explorer.exe", "\"" + path + "\"");
		}

		private static TextBlock Text(string value, double size, string ink, bool bold)
		{
			TextBlock textBlock = new TextBlock();
			textBlock.Text = value;
			textBlock.FontSize = size;
			textBlock.SetResourceReference(TextBlock.ForegroundProperty, ink);
			textBlock.FontWeight = (bold ? FontWeights.SemiBold : FontWeights.Normal);
			textBlock.TextWrapping = TextWrapping.Wrap;
			textBlock.VerticalAlignment = VerticalAlignment.Center;
			return textBlock;
		}

		private static void Display(StackPanel card, string title, UsageSlot slot)
		{
			PlanReport report = slot.Report;
			card.Children.Clear();
			card.Children.Add(Text(title, 14.0, Ink, true));
			if (report.Rows == null || report.Rows.Count == 0)
			{
				TextBlock textBlock = Text(report.Note ?? "尚未读取官方套餐数据。", 13.0, Muted, false);
				textBlock.Margin = new Thickness(0.0, 10.0, 0.0, 2.0);
				card.Children.Add(textBlock);
			}
			else
			{
				foreach (Allowance item in report.Rows.Where((Allowance x) => x != null))
				{
					DockPanel dockPanel = new DockPanel();
					dockPanel.Margin = new Thickness(0.0, 7.0, 0.0, 1.0);
					DockPanel dockPanel2 = dockPanel;
					TextBlock textBlock2 = Text(slot.Expired(item) ? "待刷新" : ("剩余 " + item.Remaining.ToString("0.#", CultureInfo.CurrentCulture) + "%"), 16.0, Ink, true);
					textBlock2.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
					DockPanel.SetDock(textBlock2, Dock.Right);
					dockPanel2.Children.Add(textBlock2);
					StackPanel stackPanel = new StackPanel();
					stackPanel.Children.Add(Text(item.Window, 13.0, Ink, false));
					stackPanel.Children.Add(Text(UsageSlot.ResetText(item), 12.0, Muted, false));
					dockPanel2.Children.Add(stackPanel);
					card.Children.Add(dockPanel2);
				}
			}
			if (!string.IsNullOrWhiteSpace(slot.Error))
			{
				TextBlock textBlock3 = Text(slot.Error, 11.0, Muted, false);
				textBlock3.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
				card.Children.Add(textBlock3);
			}
		}

		private async Task Refresh()
		{
			if (reading || closed)
			{
				return;
			}
			reading = true;
			Button button = Control<Button>("Refresh");
			button.IsEnabled = false;
			button.Content = "正在读取…";
			try
			{
				await Task.WhenAll(desk.RefreshQuota(0), desk.RefreshQuota(1));
			}
			finally
			{
				reading = false;
				if (!closed)
				{
					button.IsEnabled = true;
					button.Content = "刷新额度";
				}
			}
		}

		private Handoff Selected()
		{
			int selectedIndex = list.SelectedIndex;
			if (selectedIndex < 0 || selectedIndex >= records.Length)
			{
				return null;
			}
			return records[selectedIndex];
		}

		private void Reload()
		{
			Handoff old = Selected();
			records = RelayStore.All().ToArray();
			list.Items.Clear();
			Handoff[] array = records;
			foreach (Handoff handoff in array)
			{
				DockPanel dockPanel = new DockPanel();
				TextBlock textBlock = Text(handoff.StageLabel(), 12.0, Muted, false);
				textBlock.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
				DockPanel.SetDock(textBlock, Dock.Right);
				dockPanel.Children.Add(textBlock);
				TextBlock textBlock2 = Text(handoff.Name, 13.0, Ink, false);
				textBlock2.TextWrapping = TextWrapping.NoWrap;
				textBlock2.TextTrimming = TextTrimming.CharacterEllipsis;
				textBlock2.ToolTip = handoff.Name;
				dockPanel.Children.Add(textBlock2);
				list.Items.Add(new ListBoxItem
				{
					Content = dockPanel
				});
			}
			if (records.Length > 0)
			{
				list.SelectedIndex = ((old != null) ? Math.Max(0, Array.FindIndex(records, (Handoff j) => j.Id == old.Id)) : 0);
			}
			RecordDetail();
		}

		private void RecordDetail()
		{
			Handoff handoff = Selected();
			TextBlock textBlock = Control<TextBlock>("LogDetail");
			textBlock.Text = ((handoff == null) ? "还没有交接记录。" : (handoff.From + " → " + handoff.To + "  ·  " + new DateTime(handoff.Created, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") + "\n" + handoff.Detail));
			textBlock.ToolTip = textBlock.Text;
			bool flag = handoff != null && (handoff.Stage == "paired" || handoff.Stage == "unpaired" || handoff.Stage == "pair-missing");
			bool flag2 = handoff != null && handoff.Stage != "done" && handoff.Stage != "cancel" && !flag;
			Control<Button>("Files").Visibility = ((handoff == null || flag) ? Visibility.Collapsed : Visibility.Visible);
			Control<Button>("Resume").Visibility = ((!flag2) ? Visibility.Collapsed : Visibility.Visible);
			Control<Button>("CancelJob").Visibility = ((!flag2 || !(handoff.Stage != "confirm") || !(handoff.Stage != "return-confirm")) ? Visibility.Collapsed : Visibility.Visible);
		}

		private void Change(bool cancel)
		{
			Handoff handoff = Selected();
			if (handoff == null || desk.RelayBusy)
			{
				return;
			}
			handoff = RelayStore.Load(handoff.Id);
			if (handoff.Stage == "linked" && handoff.LinkedId != null)
			{
				handoff = RelayStore.Load(handoff.LinkedId);
			}
			if (!(handoff.Stage == "done") && !(handoff.Stage == "cancel") && (!cancel || (!(handoff.Stage == "confirm") && !(handoff.Stage == "return-confirm"))))
			{
				if (cancel)
				{
					handoff.Stage = "cancel";
					handoff.Detail = "已停止后续操作。";
				}
				else
				{
					handoff.Detail = "已安排继续处理。";
				}
				handoff.Held = false;
				RelayStore.Save(handoff);
				Reload();
				if (!cancel)
				{
					window.Hide();
				}
			}
		}
	}
}
