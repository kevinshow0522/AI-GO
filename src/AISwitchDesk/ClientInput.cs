using System;
using System.Threading;

namespace AISwitchDesk
{
	internal static class ClientInput
	{
		internal static bool TryUse(string app, Action action)
		{
			using (Mutex mutex = new Mutex(false, "Local\\AISwitchDesk.ClientInput." + app))
			{
				try
				{
					if (!mutex.WaitOne(0))
					{
						return false;
					}
				}
				catch (AbandonedMutexException)
				{
				}
				try
				{
					action();
					return true;
				}
				finally
				{
					mutex.ReleaseMutex();
				}
			}
		}
	}
}
