using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AISwitchDesk
{
	internal static class Glass
	{
		private struct Margins
		{
			internal int Left;

			internal int Right;

			internal int Top;

			internal int Bottom;
		}

		private struct DibHeader
		{
			internal uint Size;

			internal int Width;

			internal int Height;

			internal ushort Planes;

			internal ushort Bits;

			internal uint Compression;

			internal uint ImageSize;

			internal int XPixelsPerMeter;

			internal int YPixelsPerMeter;

			internal uint ColorsUsed;

			internal uint ImportantColors;
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

		[DllImport("dwmapi.dll")]
		private static extern int DwmIsCompositionEnabled(out bool enabled);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int diameterX, int diameterY);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr handle);

		[DllImport("user32.dll")]
		private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

		internal static void RoundWindow(IntPtr handle, double radius)
		{
			Native.Rect rect;
			if (!(handle == IntPtr.Zero) && Native.GetWindowRect(handle, out rect) && rect.Width > 0 && rect.Height > 0)
			{
				int num = (int)Math.Round(2.0 * radius * (double)Native.GetDpiForWindow(handle) / 96.0);
				IntPtr intPtr = CreateRoundRectRgn(-1, -1, rect.Width + 2, rect.Height + 2, num + 2, num + 2);
				if (intPtr != IntPtr.Zero && SetWindowRgn(handle, intPtr, true) == 0)
				{
					DeleteObject(intPtr);
				}
			}
		}

		[DllImport("gdi32.dll")]
		private static extern int SetDIBitsToDevice(IntPtr dc, int x, int y, uint width, uint height, int sourceX, int sourceY, uint start, uint lines, IntPtr bits, ref DibHeader info, uint colorMode);

		internal static void PaintRail(Graphics target, Rectangle bounds, int offset, int railHeight, bool glass, Action<Graphics> details, int radius = 0, bool nativeBackdrop = false)
		{
			Size size = bounds.Size;
			if (size.Width < 1 || size.Height < 1)
			{
				return;
			}
			Rectangle rect = new Rectangle(Point.Empty, size);
			using (Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
			{
				using (Graphics graphics = Graphics.FromImage(bitmap))
				{
					DeskTheme theme = DeskTheme.Current;
					using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, -offset, size.Width, Math.Max(1, railHeight)), Color.FromArgb((glass && !nativeBackdrop) ? 232 : 255, theme.RailTop), Color.FromArgb((glass && !nativeBackdrop) ? 242 : 255, theme.RailBottom), 90f))
					{
						graphics.CompositingMode = CompositingMode.SourceCopy;
						if (radius > 0)
						{
							graphics.FillRectangle(brush, 0, -offset, size.Width, railHeight);
						}
						else
						{
							graphics.FillRectangle(brush, rect);
						}
						graphics.CompositingMode = CompositingMode.SourceOver;
						if (details != null)
						{
							details(graphics);
						}
					}
				}
				BitmapData bitmapData = bitmap.LockBits(rect, (radius <= 0 || !glass) ? ImageLockMode.ReadOnly : ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
				try
				{
					if (radius > 0 && glass && offset <= 0 && railHeight > 0 && railHeight - offset <= size.Height)
					{
						SoftenCorners(bitmapData, new Size(size.Width, railHeight), radius, -offset);
					}
					DibHeader dibHeader = default(DibHeader);
					dibHeader.Size = (uint)Marshal.SizeOf(typeof(DibHeader));
					dibHeader.Width = size.Width;
					dibHeader.Height = -size.Height;
					dibHeader.Planes = 1;
					dibHeader.Bits = 32;
					DibHeader info = dibHeader;
					IntPtr hdc = target.GetHdc();
					try
					{
						SetDIBitsToDevice(hdc, bounds.X, bounds.Y, (uint)size.Width, (uint)size.Height, 0, 0, 0u, (uint)size.Height, bitmapData.Scan0, ref info, 0u);
					}
					finally
					{
						target.ReleaseHdc(hdc);
					}
				}
				finally
				{
					bitmap.UnlockBits(bitmapData);
				}
			}
		}

		private static void SoftenCorners(BitmapData pixels, Size size, int requestedRadius, int offsetY)
		{
			double num = Math.Min(requestedRadius, (double)Math.Min(size.Width, size.Height) * 0.5);
			for (int i = 0; i < (int)Math.Ceiling(num); i++)
			{
				for (int j = 0; j < (int)Math.Ceiling(num); j++)
				{
					double num2 = num - (double)j - 0.5;
					double num3 = num - (double)i - 0.5;
					double num4 = Math.Sqrt(num2 * num2 + num3 * num3);
					if (num4 < num - 0.71)
					{
						break;
					}
					int num5 = 0;
					if (num4 < num + 0.71)
					{
						for (int k = 0; k < 8; k++)
						{
							for (int l = 0; l < 8; l++)
							{
								double num6 = num - (double)j - ((double)l + 0.5) / 8.0;
								double num7 = num - (double)i - ((double)k + 0.5) / 8.0;
								if (num6 * num6 + num7 * num7 <= num * num)
								{
									num5++;
								}
							}
						}
					}
					if (num5 != 64)
					{
						int num8 = 4 * j;
						int num9 = 4 * (size.Width - 1 - j);
						int num10 = (offsetY + i) * pixels.Stride;
						int num11 = (offsetY + size.Height - 1 - i) * pixels.Stride;
						CoverPixel(pixels.Scan0, num10 + num8, num5);
						CoverPixel(pixels.Scan0, num10 + num9, num5);
						CoverPixel(pixels.Scan0, num11 + num8, num5);
						CoverPixel(pixels.Scan0, num11 + num9, num5);
					}
				}
			}
		}

		private static void CoverPixel(IntPtr pixels, int offset, int coverage)
		{
			if (coverage == 0)
			{
				Marshal.WriteInt32(pixels, offset, 0);
				return;
			}
			for (int i = 0; i < 4; i++)
			{
				int ofs = offset + i;
				int num = Marshal.ReadByte(pixels, ofs);
				Marshal.WriteByte(pixels, ofs, (byte)((num * coverage + 32) / 64));
			}
		}

		internal static bool Apply(IntPtr window)
		{
			bool enabled;
			if (SystemInformation.HighContrast || DwmIsCompositionEnabled(out enabled) != 0 || !enabled || object.Equals(Registry.GetValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "EnableTransparency", 1), 0))
			{
				return false;
			}
			int value = 1;
			Native.DwmSetWindowAttribute(window, 33, ref value, 4);
			Margins margins = default(Margins);
			margins.Left = -1;
			margins.Top = -1;
			margins.Right = -1;
			margins.Bottom = -1;
			Margins margins2 = margins;
			return DwmExtendFrameIntoClientArea(window, ref margins2) == 0;
		}
	}
}
