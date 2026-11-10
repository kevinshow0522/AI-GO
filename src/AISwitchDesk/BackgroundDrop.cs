using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AISwitchDesk
{
    // UI Automation does not need keyboard focus. Keep any native file picker
    // behind the user's working window, including when the AI was minimized.
    internal sealed class BackgroundDrop : IDisposable
    {
        private readonly IntPtr target;
        private IntPtr workingWindow;
        private readonly bool minimized;
        private Native.Placement original;
        private readonly Timer timer;
        private readonly object sync = new object();
        private bool disposed;

        internal BackgroundDrop(IntPtr handle, IntPtr foreground)
        {
            target = handle;
            workingWindow = foreground;
            minimized = Native.IsIconic(target);
            original.Length = Marshal.SizeOf(typeof(Native.Placement));
            Native.GetWindowPlacement(target, ref original);
            if (minimized) Native.ShowWindowAsync(target, 4); // SW_SHOWNOACTIVATE
            Pulse(null);
            timer = new Timer(Pulse, null, 0, 80);
        }

        private bool BelongsToTarget(IntPtr window)
        {
            for (int i = 0; window != IntPtr.Zero && i < 12; i++)
            {
                if (window == target) return true;
                window = Native.GetWindow(window, 4); // GW_OWNER
            }
            return false;
        }

        private void Pulse(object unused)
        {
            lock (sync)
            {
                if (disposed || !Native.IsWindow(target)) return;
                IntPtr foreground = Native.GetForegroundWindow();
                if (foreground != IntPtr.Zero && !BelongsToTarget(foreground))
                    workingWindow = foreground;
                if (workingWindow == IntPtr.Zero || !Native.IsWindow(workingWindow) || BelongsToTarget(workingWindow)) return;
                if (BelongsToTarget(foreground))
                {
                    Native.SetWindowPos(foreground, workingWindow, 0, 0, 0, 0, 19);
                    Native.SetForegroundWindow(workingWindow);
                }
                Native.SetWindowPos(target, workingWindow, 0, 0, 0, 0, 19); // no size/move/activate
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            lock (sync)
            {
                if (disposed) return;
                Pulse(null);
                disposed = true;
                if (minimized && Native.IsWindow(target))
                    Native.SetWindowPlacement(target, ref original);
            }
        }
    }
}
