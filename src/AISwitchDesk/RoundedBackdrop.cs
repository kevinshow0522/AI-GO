using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace AISwitchDesk
{
	internal sealed class RoundedBackdrop : IDisposable
	{
		private struct QueueOptions
		{
			internal int Size;

			internal int ThreadType;

			internal int Apartment;
		}

		[ComImport]
		[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IDesktopInterop
		{
			void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
		}

		private static Compositor compositor;

		private static object controller;

		private DesktopWindowTarget target;

		private SpriteVisual surface;

		private CompositionRoundedRectangleGeometry outline;

		private CompositionGeometricClip clip;

		private CompositionBackdropBrush brush;

		private CompositionEffectBrush frost;

		private CompositionEffectFactory factory;

		private bool disposed;

		[DllImport("CoreMessaging.dll")]
		private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);

		internal static RoundedBackdrop Attach(IntPtr handle, float opacity)
		{
			int value = 1;
			if (Native.DwmSetWindowAttribute(handle, 17, ref value, 4) != 0)
			{
				return null;
			}
			RoundedBackdrop roundedBackdrop = new RoundedBackdrop();
			try
			{
				if (compositor == null)
				{
					if (DispatcherQueue.GetForCurrentThread() == null)
					{
						QueueOptions queueOptions = default(QueueOptions);
						queueOptions.Size = 12;
						queueOptions.ThreadType = 2;
						queueOptions.Apartment = 2;
						QueueOptions options = queueOptions;
						IntPtr pUnk;
						Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out pUnk));
						try
						{
							controller = Marshal.GetObjectForIUnknown(pUnk);
						}
						finally
						{
							Marshal.Release(pUnk);
						}
					}
					compositor = new Compositor();
				}
				IntPtr pUnk2;
				((IDesktopInterop)(object)compositor).CreateDesktopWindowTarget(handle, true, out pUnk2);
				try
				{
					roundedBackdrop.target = (DesktopWindowTarget)Marshal.GetObjectForIUnknown(pUnk2);
				}
				finally
				{
					Marshal.Release(pUnk2);
				}
				roundedBackdrop.brush = compositor.CreateHostBackdropBrush();
				CompositionEffectSourceParameter input = new CompositionEffectSourceParameter("Desktop");
				roundedBackdrop.factory = compositor.CreateEffectFactory(new FrostEffect(input));
				roundedBackdrop.frost = roundedBackdrop.factory.CreateBrush();
				roundedBackdrop.frost.SetSourceParameter("Desktop", roundedBackdrop.brush);
				roundedBackdrop.outline = compositor.CreateRoundedRectangleGeometry();
				roundedBackdrop.clip = compositor.CreateGeometricClip(roundedBackdrop.outline);
				roundedBackdrop.surface = compositor.CreateSpriteVisual();
				roundedBackdrop.surface.Brush = roundedBackdrop.frost;
				roundedBackdrop.surface.Clip = roundedBackdrop.clip;
				roundedBackdrop.surface.Opacity = opacity;
				roundedBackdrop.target.Root = roundedBackdrop.surface;
				return roundedBackdrop;
			}
			catch (COMException)
			{
				roundedBackdrop.Dispose();
				return null;
			}
			catch (InvalidCastException)
			{
				roundedBackdrop.Dispose();
				return null;
			}
		}

		internal void Resize(int width, int height, float radius, int top = 0)
		{
			Vector2 vector = new Vector2(width, height);
			Vector2 vector2 = new Vector2(Math.Min(radius, (float)Math.Min(width, height) * 0.5f));
			Vector3 vector3 = new Vector3(0f, top, 0f);
			if (!(surface.Offset == vector3) || !(surface.Size == vector) || !(outline.CornerRadius == vector2))
			{
				outline.Size = vector;
				outline.CornerRadius = vector2;
				surface.Size = vector;
				surface.Offset = vector3;
			}
		}

		public void Dispose()
		{
			if (!disposed)
			{
				disposed = true;
				if (target != null)
				{
					target.Dispose();
				}
				if (surface != null)
				{
					surface.Dispose();
				}
				if (clip != null)
				{
					clip.Dispose();
				}
				if (outline != null)
				{
					outline.Dispose();
				}
				if (frost != null)
				{
					frost.Dispose();
				}
				if (factory != null)
				{
					factory.Dispose();
				}
				if (brush != null)
				{
					brush.Dispose();
				}
				GC.KeepAlive(controller);
			}
		}
	}
}
