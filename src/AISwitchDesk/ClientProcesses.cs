using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace AISwitchDesk
{
	internal sealed class ClientProcesses : IDisposable
	{
		private sealed class Row
		{
			internal int Id;

			internal int Parent;

			internal string Image;
		}

		private readonly string name;

		private readonly int session;

		private readonly Dictionary<int, Process> owned = new Dictionary<int, Process>();

		private readonly HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		internal ClientProcesses(string name)
		{
			this.name = name;
			using (Process process = Process.GetCurrentProcess())
			{
				session = process.SessionId;
			}
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (name == "ChatGPT")
			{
				AddFolder(Path.Combine(folderPath, "OpenAI", "Codex"));
				AddFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes"));
			}
			else if (name == "Claude")
			{
				AddFolder(Path.Combine(folderPath, "Claude-Data", ClientApps.ClaudeFamily));
			}
		}

		private void AddFolder(string path)
		{
			folders.Add(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
		}

		private bool Add(Row row, Process parent)
		{
			if (owned.ContainsKey(row.Id))
			{
				return false;
			}
			Process process = null;
			try
			{
				process = Process.GetProcessById(row.Id);
				if (process.Handle == IntPtr.Zero || process.HasExited || !string.Equals(Native.ImageOf((uint)row.Id), row.Image, StringComparison.OrdinalIgnoreCase) || (parent != null && process.StartTime < parent.StartTime))
				{
					return false;
				}
				owned.Add(row.Id, process);
				process = null;
				return true;
			}
			catch (ArgumentException)
			{
				return false;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			finally
			{
				if (process != null)
				{
					process.Dispose();
				}
			}
		}

		private void Scan()
		{
			List<Row> list = new List<Row>();
			using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,ExecutablePath FROM Win32_Process WHERE SessionId=" + session))
			{
				using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
				{
					foreach (ManagementObject item in managementObjectCollection)
					{
						using (item)
						{
							string text = item["ExecutablePath"] as string;
							if (!string.IsNullOrEmpty(text))
							{
								list.Add(new Row
								{
									Id = Convert.ToInt32(item["ProcessId"]),
									Parent = Convert.ToInt32(item["ParentProcessId"]),
									Image = text
								});
							}
						}
					}
				}
			}
			foreach (Row item2 in list)
			{
				if (string.Equals(Path.GetFileNameWithoutExtension(item2.Image), name, StringComparison.OrdinalIgnoreCase) && ClientApps.OwnsProcess(name, (uint)item2.Id) && Add(item2, null))
				{
					AddFolder(Path.GetDirectoryName(item2.Image));
				}
			}
			bool flag;
			do
			{
				flag = false;
				foreach (Row row in list)
				{
					Process value;
					if (!owned.ContainsKey(row.Id) && owned.TryGetValue(row.Parent, out value) && folders.Any((string path) => row.Image.StartsWith(path, StringComparison.OrdinalIgnoreCase)) && Add(row, value))
					{
						flag = true;
					}
				}
			}
			while (flag);
		}

		private bool WaitForExit(int milliseconds)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (owned.Values.Any((Process p) => !p.HasExited))
			{
				if (stopwatch.ElapsedMilliseconds >= milliseconds)
				{
					return false;
				}
				Thread.Sleep(60);
			}
			return true;
		}

		internal void Quit(bool launchPending)
		{
			DateTime dateTime = DateTime.UtcNow.AddSeconds(6.0);
			Scan();
			while (launchPending && owned.Count == 0 && DateTime.UtcNow < dateTime)
			{
				Thread.Sleep(180);
				Scan();
			}
			if (owned.Count == 0)
			{
				return;
			}
			foreach (Process value in owned.Values)
			{
				try
				{
					if (!value.HasExited)
					{
						value.CloseMainWindow();
					}
				}
				catch (InvalidOperationException)
				{
				}
			}
			WaitForExit(1600);
			for (int i = 0; i < 3; i++)
			{
				Scan();
				if (WaitForExit(0))
				{
					return;
				}
				foreach (Process value2 in owned.Values)
				{
					try
					{
						if (!value2.HasExited)
						{
							value2.Kill();
						}
					}
					catch (InvalidOperationException)
					{
					}
					catch (Win32Exception)
					{
						if (!value2.HasExited)
						{
							throw;
						}
					}
				}
				WaitForExit(700);
			}
			Scan();
			if (WaitForExit(0))
			{
				return;
			}
			throw new InvalidOperationException(name + " 仍有后台进程在运行。");
		}

		public void Dispose()
		{
			foreach (Process value in owned.Values)
			{
				value.Dispose();
			}
		}
	}
}
