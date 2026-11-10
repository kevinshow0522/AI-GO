using System;
using System.Threading;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal static class Program
	{
		[STAThread]
		private static void Main(string[] args)
		{
			if (args.Length == 1 && args[0] == "--background")
			{
				BackgroundRelay.Run();
				return;
			}
			if (args.Length == 1 && args[0] == "--stop-background")
			{
				BackgroundRelay.Stop();
				return;
			}
			if (args.Length == 2 && args[0] == "--mcp" && (args[1] == "ChatGPT" || args[1] == "Claude"))
			{
				BackgroundRelay.EnsureRunning();
				RelayMcp.Run(args[1]);
				return;
			}
			BackgroundRelay.EnsureRunning();
			bool createdNew;
			using (Mutex mutex = new Mutex(true, "Local\\AISwitchDesk.0649A98C", out createdNew))
			{
				if (!createdNew)
				{
					IntPtr intPtr = Native.FindWindow(null, "AI 双窗");
					if (intPtr != IntPtr.Zero)
					{
						if (Native.IsIconic(intPtr))
						{
							Native.ShowWindowAsync(intPtr, 9);
						}
						Native.SetForegroundWindow(intPtr);
					}
					return;
				}
				try
				{
					Application.EnableVisualStyles();
					Application.SetCompatibleTextRenderingDefault(false);
					using (DeskForm mainForm = new DeskForm())
					{
						Application.Run(mainForm);
					}
				}
				finally
				{
					mutex.ReleaseMutex();
				}
			}
		}
	}
}
