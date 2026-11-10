using System;
using System.Runtime.InteropServices;

namespace AISwitchDesk
{
	internal sealed class OutsideClick : IDisposable
	{
		private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);

		private readonly Func<IntPtr, bool> contains;

		private readonly Action dismiss;

		private readonly MouseHook callback;

		private IntPtr hook;

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr SetWindowsHookEx(int id, MouseHook hook, IntPtr module, uint thread);

		[DllImport("user32.dll")]
		private static extern bool UnhookWindowsHookEx(IntPtr hook);

		[DllImport("user32.dll")]
		private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

		[DllImport("user32.dll")]
		private static extern IntPtr WindowFromPoint(Native.Point point);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandle(string name);

		internal OutsideClick(Func<IntPtr, bool> contains, Action dismiss)
		{
			this.contains = contains;
			this.dismiss = dismiss;
			callback = OnClick;
		}

		internal void Watch()
		{
			if (hook == IntPtr.Zero)
			{
				hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0u);
			}
		}

		private IntPtr OnClick(int code, IntPtr message, IntPtr data)
		{
			int num = message.ToInt32();
			if (code >= 0 && (num == 513 || num == 516 || num == 519 || num == 523))
			{
				Native.Point point = default(Native.Point);
				point.X = Marshal.ReadInt32(data);
				point.Y = Marshal.ReadInt32(data, 4);
				Native.Point point2 = point;
				if (!contains(Native.GetAncestor(WindowFromPoint(point2), 2u)))
				{
					dismiss();
				}
			}
			return CallNextHookEx(IntPtr.Zero, code, message, data);
		}

		public void Dispose()
		{
			if (hook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(hook);
				hook = IntPtr.Zero;
			}
		}
	}
}
