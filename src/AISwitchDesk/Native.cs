using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AISwitchDesk
{
	internal static class Native
	{
		internal delegate bool WindowVisitor(IntPtr window, IntPtr data);

		internal struct Point
		{
			internal int X;

			internal int Y;
		}

		internal struct Rect
		{
			internal int Left;

			internal int Top;

			internal int Right;

			internal int Bottom;

			internal int Width
			{
				get
				{
					return Right - Left;
				}
			}

			internal int Height
			{
				get
				{
					return Bottom - Top;
				}
			}
		}

		internal struct Placement
		{
			internal int Length;

			internal int Flags;

			internal int ShowCmd;

			internal Point MinPosition;

			internal Point MaxPosition;

			internal Rect NormalPosition;
		}

		private struct Startup
		{
			internal uint Size;

			internal IntPtr Reserved;

			internal IntPtr Desktop;

			internal IntPtr Title;

			internal uint X;

			internal uint Y;

			internal uint Width;

			internal uint Height;

			internal uint Columns;

			internal uint Rows;

			internal uint Fill;

			internal uint Flags;

			internal ushort Show;

			internal ushort ReservedBytes;

			internal IntPtr ReservedData;

			internal IntPtr Input;

			internal IntPtr Output;

			internal IntPtr Error;
		}

		private struct ProcessHandles
		{
			internal IntPtr Process;

			internal IntPtr Thread;

			internal uint ProcessId;

			internal uint ThreadId;
		}

		internal const int Style = -16;

		internal const int ExStyle = -20;

		internal const int MaxBox = 65536;

		internal const int ToolWindow = 128;

		internal const int AppWindow = 262144;

		internal const uint NoZOrder = 4u;

		internal const uint NoActivate = 16u;

		internal const int Restore = 9;

		internal const uint Root = 2u;

		internal const uint Owner = 4u;

		[DllImport("user32.dll")]
		internal static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern int GetClassName(IntPtr window, StringBuilder result, int max);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern int GetWindowText(IntPtr window, StringBuilder result, int max);

		[DllImport("user32.dll")]
		internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

		[DllImport("user32.dll")]
		internal static extern IntPtr GetWindow(IntPtr window, uint command);

		[DllImport("user32.dll")]
		internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

		[DllImport("user32.dll")]
		internal static extern bool IsChild(IntPtr parent, IntPtr window);

		[DllImport("user32.dll")]
		internal static extern IntPtr GetDlgItem(IntPtr dialog, int id);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		internal static extern bool IsWindowEnabled(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool ScreenToClient(IntPtr window, ref Point point);

		[DllImport("user32.dll")]
		internal static extern IntPtr ChildWindowFromPointEx(IntPtr window, Point point, uint flags);


		[DllImport("user32.dll")]
		internal static extern bool IsWindow(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool IsIconic(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool ShowWindowAsync(IntPtr window, int command);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);

		[DllImport("user32.dll")]
		internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

		[DllImport("user32.dll")]
		internal static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool GetWindowPlacement(IntPtr window, ref Placement position);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool SetWindowPlacement(IntPtr window, ref Placement position);

		[DllImport("user32.dll")]
		internal static extern bool IsHungAppWindow(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern IntPtr BeginDeferWindowPos(int count);

		[DllImport("user32.dll")]
		internal static extern IntPtr DeferWindowPos(IntPtr group, IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

		[DllImport("user32.dll")]
		internal static extern bool EndDeferWindowPos(IntPtr group);

		[DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
		internal static extern int FrameBounds(IntPtr window, int attribute, out Rect rect, int length);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
		internal static extern int GetBits(IntPtr window, int index);

		[DllImport("user32.dll")]
		internal static extern uint GetDpiForWindow(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		internal static extern bool SetForegroundWindow(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

		[DllImport("user32.dll")]
		internal static extern bool UnregisterHotKey(IntPtr window, int id);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern IntPtr FindWindow(string cls, string name);

		[DllImport("kernel32.dll")]
		private static extern IntPtr OpenProcess(uint rights, bool inherit, uint id);

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessHandles handles);

		internal static void StartBackground(string executable)
		{
			Startup startup = default(Startup);
			startup.Size = (uint)Marshal.SizeOf(typeof(Startup));
			startup.Flags = 1u;
			startup.Show = 0;
			Startup startup2 = startup;
			ProcessHandles handles;
			if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" --background"), IntPtr.Zero, IntPtr.Zero, false, 134217728u, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup2, out handles))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			CloseHandle(handles.Thread);
			CloseHandle(handles.Process);
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint length);

		internal static string ImageOf(uint pid)
		{
			IntPtr intPtr = OpenProcess(4096u, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return "";
			}
			try
			{
				uint length = 4096u;
				StringBuilder stringBuilder = new StringBuilder((int)length);
				return QueryFullProcessImageName(intPtr, 0u, stringBuilder, ref length) ? stringBuilder.ToString() : "";
			}
			finally
			{
				CloseHandle(intPtr);
			}
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder name);

		[DllImport("dwmapi.dll")]
		internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int length);

		[DllImport("dwmapi.dll")]
		internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int length);

		internal static string PackageOf(uint pid)
		{
			IntPtr intPtr = OpenProcess(4096u, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return "";
			}
			try
			{
				uint length = 256u;
				StringBuilder stringBuilder = new StringBuilder((int)length);
				return (GetPackageFamilyName(intPtr, ref length, stringBuilder) == 0) ? stringBuilder.ToString() : "";
			}
			finally
			{
				CloseHandle(intPtr);
			}
		}
	}
}
