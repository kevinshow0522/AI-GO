using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;

namespace AISwitchDesk
{
	internal sealed class AppExitMenu : IDisposable
	{
		private readonly Window window;

		private readonly System.Windows.Controls.Button item;

		private readonly OutsideClick outside;

		private readonly IntPtr handle;

		private int index;

		private bool disposed;

		internal AppExitMenu(DeskForm owner, Action<int> quit)
		{
			AppExitMenu appExitMenu = this;
			using (Stream stream = typeof(AppExitMenu).Assembly.GetManifestResourceStream("ExitMenu.xaml"))
			{
				window = (Window)XamlReader.Load(stream);
			}
			ApplyTheme();
			item = (System.Windows.Controls.Button)window.FindName("Quit");
			WindowInteropHelper windowInteropHelper = new WindowInteropHelper(window)
			{
				Owner = owner.Handle
			};
			handle = windowInteropHelper.EnsureHandle();
			outside = new OutsideClick((IntPtr root) => root == handle, Hide);
			window.IsVisibleChanged += delegate
			{
				if (window.IsVisible)
				{
					outside.Watch();
				}
				else
				{
					outside.Dispose();
				}
			};
			window.Deactivated += delegate
			{
				Hide();
			};
			window.Closing += delegate(object sender, CancelEventArgs e)
			{
				if (!disposed)
				{
					e.Cancel = true;
					window.Dispatcher.BeginInvoke(new Action(Hide));
				}
			};
			window.PreviewMouseDown += delegate
			{
				if (!item.IsMouseOver)
				{
					Hide();
				}
			};
			ElementHost.EnableModelessKeyboardInterop(window);
			window.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e)
			{
				if (e.Key == Key.Escape)
				{
					e.Handled = true;
					Hide();
				}
			};
			HwndSource.FromHwnd(handle).AddHook(KeyMessage);
			item.Click += delegate
			{
				int obj = appExitMenu.index;
				appExitMenu.Hide();
				quit(obj);
			};
		}

		internal void ApplyTheme()
		{
			DeskTheme.Current.ApplyResources(window.Resources);
		}

		internal void Show(int target, System.Drawing.Point anchor, bool enabled, bool fromKeyboard)
		{
			index = target;
			item.IsEnabled = enabled;
			Rectangle workingArea = Screen.FromPoint(anchor).WorkingArea;
			uint dpiForWindow = Native.GetDpiForWindow(handle);
			PlaceAt(anchor, workingArea, dpiForWindow);
			uint dpiForWindow2 = Native.GetDpiForWindow(handle);
			if (dpiForWindow2 != dpiForWindow)
			{
				PlaceAt(anchor, workingArea, dpiForWindow2);
			}
			if (!window.IsVisible)
			{
				window.Show();
			}
			window.Activate();
			if (fromKeyboard && enabled)
			{
				item.Focus();
			}
		}

		private void PlaceAt(System.Drawing.Point anchor, Rectangle area, uint dpi)
		{
			double num = (double)((dpi == 0) ? 96u : dpi) / 96.0;
			int num2 = (int)Math.Round(window.Width * num);
			int num3 = (int)Math.Round(window.Height * num);
			int val = ((anchor.X + num2 <= area.Right) ? anchor.X : (anchor.X - num2));
			int val2 = ((anchor.Y + num3 <= area.Bottom) ? anchor.Y : (anchor.Y - num3));
			val = Math.Max(area.Left, Math.Min(val, area.Right - num2));
			val2 = Math.Max(area.Top, Math.Min(val2, area.Bottom - num3));
			Native.SetWindowPos(handle, IntPtr.Zero, val, val2, num2, num3, 20u);
		}

		private IntPtr KeyMessage(IntPtr h, int msg, IntPtr key, IntPtr data, ref bool handled)
		{
			if (msg == 256 && key == (IntPtr)27)
			{
				handled = true;
				Hide();
			}
			return IntPtr.Zero;
		}

		internal void Hide()
		{
			if (!disposed && window.IsVisible)
			{
				window.Hide();
			}
		}

		public void Dispose()
		{
			if (!disposed)
			{
				disposed = true;
				outside.Dispose();
				window.Close();
			}
		}
	}
}
