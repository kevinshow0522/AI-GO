using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AISwitchDesk
{
	internal sealed class ClientWindow
	{
		internal readonly string Name;

		internal readonly string AppId;

		private readonly string family;

		private IntPtr lastWindow;

		private uint lastProcess;

		internal bool LaunchPending;

		internal bool Quitting;

		internal bool OpenAfterQuit;

		internal Task Starting;

		internal DateTime LaunchDeadline;

		internal ClientWindow(string name, string familyName, string entry)
		{
			Name = name;
			family = familyName;
			AppId = familyName + "!" + entry;
		}

		internal void Quit(bool launchPending)
		{
			using (ClientProcesses clientProcesses = new ClientProcesses(Name, family))
			{
				clientProcesses.Quit(launchPending);
			}
		}

		internal void Launch()
		{
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", (Name == "Claude") ? "claude-desktop.exe" : "chatgpt-desktop.exe");
			if (File.Exists(text))
			{
				try
				{
					ProcessStartInfo processStartInfo = new ProcessStartInfo(text);
					processStartInfo.UseShellExecute = false;
					processStartInfo.CreateNoWindow = true;
					using (Process.Start(processStartInfo))
					{
						return;
					}
				}
				catch (Win32Exception)
				{
				}
				try
				{
					ProcessStartInfo processStartInfo2 = new ProcessStartInfo(text);
					processStartInfo2.UseShellExecute = true;
					using (Process.Start(processStartInfo2))
					{
						return;
					}
				}
				catch (Win32Exception)
				{
				}
			}
			ProcessStartInfo processStartInfo3 = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:AppsFolder\\" + AppId);
			processStartInfo3.UseShellExecute = true;
			using (Process.Start(processStartInfo3))
			{
			}
		}

		internal IntPtr FindMainWindow()
		{
			if (IsRemembered(lastWindow))
			{
				return lastWindow;
			}
			IntPtr best = IntPtr.Zero;
			int bestScore = int.MinValue;
			Native.EnumWindows(delegate(IntPtr window, IntPtr unused)
			{
				if (!Native.IsWindowVisible(window) || Native.GetWindow(window, 4u) != IntPtr.Zero)
				{
					return true;
				}
				int bits = Native.GetBits(window, -20);
				if (((uint)bits & 0x80u) != 0)
				{
					return true;
				}
				StringBuilder stringBuilder = new StringBuilder(80);
				Native.GetClassName(window, stringBuilder, stringBuilder.Capacity);
				if (stringBuilder.ToString() != "Chrome_WidgetWin_1")
				{
					return true;
				}
				uint pid;
				Native.GetWindowThreadProcessId(window, out pid);
				if (!string.Equals(Native.PackageOf(pid), family, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				int value;
				Native.DwmGetWindowAttribute(window, 14, out value, 4);
				if (value != 0)
				{
					return true;
				}
				StringBuilder stringBuilder2 = new StringBuilder(256);
				Native.GetWindowText(window, stringBuilder2, stringBuilder2.Capacity);
				if (stringBuilder2.Length == 0)
				{
					return true;
				}
				Native.Placement placement = default(Native.Placement);
				placement.Length = Marshal.SizeOf(typeof(Native.Placement));
				Native.Placement position = placement;
				if (!Native.GetWindowPlacement(window, ref position))
				{
					return true;
				}
				int width = position.NormalPosition.Width;
				int height = position.NormalPosition.Height;
				if (width < 320 || height < 200)
				{
					return true;
				}
				int bits2 = Native.GetBits(window, -16);
				int num = Math.Min(2000, width) + Math.Min(1200, height);
				if (((uint)bits2 & 0x10000u) != 0)
				{
					num += 4000;
				}
				if (((uint)bits & 0x40000u) != 0)
				{
					num += 1000;
				}
				if (string.Equals(stringBuilder2.ToString(), Name, StringComparison.OrdinalIgnoreCase))
				{
					num += 350;
				}
				if (Native.GetForegroundWindow() == window)
				{
					num += 300;
				}
				if (num > bestScore)
				{
					best = window;
					bestScore = num;
				}
				return true;
			}, IntPtr.Zero);
			if (best != IntPtr.Zero)
			{
				Remember(best);
			}
			return best;
		}

		private bool IsRemembered(IntPtr window)
		{
			uint pid;
			if (window == IntPtr.Zero || window != lastWindow || !Native.IsWindowVisible(window) || Native.GetAncestor(window, 2u) != window || Native.GetWindow(window, 4u) != IntPtr.Zero || Native.GetWindowThreadProcessId(window, out pid) == 0 || pid != lastProcess)
			{
				return false;
			}
			int value;
			Native.DwmGetWindowAttribute(window, 14, out value, 4);
			return value == 0;
		}

		private void Remember(IntPtr window)
		{
			lastWindow = window;
			Native.GetWindowThreadProcessId(window, out lastProcess);
		}

		internal bool Recognizes(IntPtr window)
		{
			if (IsRemembered(window))
			{
				return true;
			}
			if (window == IntPtr.Zero || !Native.IsWindowVisible(window) || Native.GetWindow(window, 4u) != IntPtr.Zero || ((uint)Native.GetBits(window, -20) & 0x80u) != 0)
			{
				return false;
			}
			StringBuilder stringBuilder = new StringBuilder(80);
			Native.GetClassName(window, stringBuilder, stringBuilder.Capacity);
			if (stringBuilder.ToString() != "Chrome_WidgetWin_1")
			{
				return false;
			}
			uint pid;
			Native.GetWindowThreadProcessId(window, out pid);
			if (!string.Equals(Native.PackageOf(pid), family, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			Native.Placement placement = default(Native.Placement);
			placement.Length = Marshal.SizeOf(typeof(Native.Placement));
			Native.Placement position = placement;
			if (!Native.GetWindowPlacement(window, ref position) || position.NormalPosition.Width < 320 || position.NormalPosition.Height < 200)
			{
				return false;
			}
			Remember(window);
			return true;
		}

		internal static bool Activate(IntPtr window)
		{
			if (Native.IsIconic(window))
			{
				Native.ShowWindowAsync(window, 9);
			}
			if (!(Native.GetForegroundWindow() == window))
			{
				return Native.SetForegroundWindow(window);
			}
			return true;
		}
	}
}
