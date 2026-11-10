using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal sealed class WindowComparison
	{
		private sealed class Original
		{
			internal IntPtr Handle;

			internal uint Process;

			internal Native.Placement Placement;

			internal Native.Rect Bounds;

			internal bool StillOpen()
			{
				uint pid;
				if (Native.IsWindow(Handle) && Native.GetWindowThreadProcessId(Handle, out pid) != 0)
				{
					return pid == Process;
				}
				return false;
			}
		}

		private Original[] originals;

		internal bool Active
		{
			get
			{
				return originals != null;
			}
		}

		private static Original Capture(IntPtr handle)
		{
			Original original = new Original();
			original.Handle = handle;
			Original original2 = original;
			original2.Placement.Length = Marshal.SizeOf(typeof(Native.Placement));
			if (handle == IntPtr.Zero || Native.IsHungAppWindow(handle) || Native.GetWindowThreadProcessId(handle, out original2.Process) == 0 || !Native.GetWindowPlacement(handle, ref original2.Placement) || !Native.GetWindowRect(handle, out original2.Bounds))
			{
				throw new InvalidOperationException("有一个应用窗口尚未就绪，请稍后再试。");
			}
			return original2;
		}

		private static Rectangle VisibleFrame(IntPtr handle, Native.Rect outer)
		{
			Native.Rect rect;
			if (Native.FrameBounds(handle, 9, out rect, Marshal.SizeOf(typeof(Native.Rect))) != 0 || rect.Width <= 0 || rect.Height <= 0)
			{
				rect = outer;
			}
			return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
		}

		internal void Arrange(IntPtr gpt, IntPtr claude, Screen screen)
		{
			if (Active || gpt == claude)
			{
				throw new InvalidOperationException("无法识别两个独立窗口。");
			}
			Original[] array = new Original[2]
			{
				Capture(gpt),
				Capture(claude)
			};
			Rectangle workingArea = screen.WorkingArea;
			int num = workingArea.Left + workingArea.Width / 2;
			Rectangle[] array2 = new Rectangle[2]
			{
				Rectangle.FromLTRB(workingArea.Left, workingArea.Top, num, workingArea.Bottom),
				Rectangle.FromLTRB(num, workingArea.Top, workingArea.Right, workingArea.Bottom)
			};
			originals = array;
			try
			{
				for (int i = 0; i < array.Length; i++)
				{
					if (array[i].Placement.ShowCmd != 1)
					{
						Native.Placement position = array[i].Placement;
						Rectangle rectangle = array2[i];
						rectangle.Offset(screen.Bounds.Left - workingArea.Left, screen.Bounds.Top - workingArea.Top);
						position.Flags = 0;
						position.ShowCmd = 4;
						position.NormalPosition = new Native.Rect
						{
							Left = rectangle.Left,
							Top = rectangle.Top,
							Right = rectangle.Right,
							Bottom = rectangle.Bottom
						};
						if (!Native.SetWindowPlacement(array[i].Handle, ref position))
						{
							throw CannotArrange();
						}
					}
				}
				for (int j = 0; j < 2; j++)
				{
					Rectangle[] array3 = new Rectangle[2];
					bool flag = true;
					for (int k = 0; k < 2; k++)
					{
						Native.Rect rect;
						if (!Native.GetWindowRect(array[k].Handle, out rect))
						{
							throw CannotArrange();
						}
						Rectangle first = VisibleFrame(array[k].Handle, rect);
						Rectangle second = array2[k];
						flag &= Near(first, second);
						array3[k] = Rectangle.FromLTRB(second.Left + rect.Left - first.Left, second.Top + rect.Top - first.Top, second.Right + rect.Right - first.Right, second.Bottom + rect.Bottom - first.Bottom);
					}
					if (j > 0 && flag)
					{
						break;
					}
					IntPtr intPtr = Native.BeginDeferWindowPos(2);
					for (int l = 0; l < 2; l++)
					{
						Rectangle rectangle2 = array3[l];
						if (intPtr == IntPtr.Zero)
						{
							throw CannotArrange();
						}
						intPtr = Native.DeferWindowPos(intPtr, array[l].Handle, IntPtr.Zero, rectangle2.X, rectangle2.Y, rectangle2.Width, rectangle2.Height, 16u);
					}
					if (intPtr == IntPtr.Zero || !Native.EndDeferWindowPos(intPtr))
					{
						throw CannotArrange();
					}
				}
				for (int m = 0; m < 2; m++)
				{
					Native.Rect rect2;
					if (!Native.GetWindowRect(array[m].Handle, out rect2) || !Near(VisibleFrame(array[m].Handle, rect2), array2[m]))
					{
						throw new InvalidOperationException("当前屏幕放不下两个应用的最小窗口，已尝试还原。");
					}
				}
			}
			catch
			{
				Restore();
				throw;
			}
		}

		private static bool Near(Rectangle first, Rectangle second)
		{
			if (Math.Abs(first.Left - second.Left) <= 2 && Math.Abs(first.Top - second.Top) <= 2 && Math.Abs(first.Right - second.Right) <= 2)
			{
				return Math.Abs(first.Bottom - second.Bottom) <= 2;
			}
			return false;
		}

		private static Exception CannotArrange()
		{
			return new InvalidOperationException("未能并排显示两个窗口，可稍后重试。");
		}

		internal void Restore()
		{
			if (originals == null)
			{
				return;
			}
			bool flag = false;
			for (int i = 0; i < originals.Length; i++)
			{
				Original original = originals[i];
				if (original == null)
				{
					continue;
				}
				if (original.StillOpen())
				{
					if (Native.IsHungAppWindow(original.Handle) || !Native.SetWindowPlacement(original.Handle, ref original.Placement))
					{
						flag = true;
						continue;
					}
					if (original.Placement.ShowCmd == 1)
					{
						Native.Rect bounds = original.Bounds;
						Native.Rect rect;
						if (Native.GetWindowRect(original.Handle, out rect) && (bounds.Left != rect.Left || bounds.Top != rect.Top || bounds.Right != rect.Right || bounds.Bottom != rect.Bottom) && !Native.SetWindowPos(original.Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 20u))
						{
							flag = true;
							continue;
						}
					}
				}
				originals[i] = null;
			}
			if (flag)
			{
				throw new InvalidOperationException("有窗口暂未响应，点「还原窗口」可重试。");
			}
			originals = null;
		}
	}
}
