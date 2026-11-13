using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal static class ArtifactCapture
	{
		[DllImport("shell32.dll")]
		private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

		private static string Downloads()
		{
			Guid folder = new Guid("374DE290-123F-4565-9164-39C4925E467B");
			IntPtr path;
			if (SHGetKnownFolderPath(ref folder, 0u, IntPtr.Zero, out path) != 0)
			{
				throw new IOException("无法读取 Windows 下载目录。");
			}
			try
			{
				return Marshal.PtrToStringUni(path);
			}
			finally
			{
				Marshal.FreeCoTaskMem(path);
			}
		}

		private static bool Under(string path, string root)
		{
			return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
		}

		private static bool Regular(string path)
		{
			if (!File.Exists(path) || !Regex.IsMatch(path, "^[a-z]:[\\\\/]", RegexOptions.IgnoreCase) || path.IndexOf(':', 2) >= 0)
			{
				return false;
			}
			string text = path;
			while (!string.IsNullOrEmpty(text))
			{
				if ((File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}
				text = Path.GetDirectoryName(text);
			}
			return true;
		}

		internal static void Local(Handoff j, string path, bool explicitlyNamed)
		{
			path = Path.GetFullPath(path);
			if (!Regular(path))
			{
				throw new IOException("找不到指令所指的本机文件。");
			}
			if (!explicitlyNamed)
			{
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
				bool flag = Under(path, Path.Combine(folderPath, "Codex")) && path.IndexOf("\\outputs\\", StringComparison.OrdinalIgnoreCase) >= 0;
				FileInfo fileInfo = new FileInfo(path);
				bool flag2 = fileInfo.CreationTimeUtc.Ticks >= j.Created - TimeSpan.FromSeconds(4.0).Ticks;
				if (!flag && (!flag2 || (!Under(path, folderPath) && !Under(path, Downloads()))))
				{
					throw new IOException("此链接指向已有的本机文件。请在 GPT 指令中写明完整文件路径。");
				}
			}
			Finish(j, Path.GetFileName(path), path, null);
		}

		internal static void Finish(Handoff j, string name, string source, string text)
		{
			name = RelayStore.SafeName(name);
			if (!RelayIntent.IsFileName(name))
			{
				throw new IOException("无法确认附件的文件名和类型。");
			}
			if (j.Stage == "await-result")
			{
				using (Stream input = ((source != null) ? ((Stream)File.OpenRead(source)) : ((Stream)new MemoryStream(RelayStore.Utf8.GetBytes(text)))))
				{
					RoundTrips.Accept(j.Id, name, input);
					return;
				}
			}
			string text2 = Path.Combine(RelayStore.Folder(j.Id), "file");
			Directory.CreateDirectory(text2);
			string text3 = Path.Combine(text2, name);
			if (source != null)
			{
				string text4 = Path.Combine(text2, Guid.NewGuid().ToString("N") + ".part");
				try
				{
					using (FileStream fileStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
					{
						using (FileStream destination = new FileStream(text4, FileMode.CreateNew, FileAccess.Write, FileShare.None))
						{
							fileStream.CopyTo(destination);
						}
					}
					if (File.Exists(text3))
					{
						File.Replace(text4, text3, null);
					}
					else
					{
						File.Move(text4, text3);
					}
				}
				finally
				{
					if (File.Exists(text4))
					{
						File.Delete(text4);
					}
				}
			}
			else
			{
				RelayStore.AtomicText(text3, text);
			}
			j.Name = name;
			j.Stage = "ready";
			j.Detail = "已取得 GPT 文件，正在投递到 Claude。";
			RelayStore.Save(j);
		}

		private static string[] Matches(string name)
		{
			string path = Downloads();
			if (!Directory.Exists(path))
			{
				return new string[0];
			}
			string text = Regex.Escape(Path.GetFileNameWithoutExtension(name));
			string text2 = Regex.Escape(Path.GetExtension(name));
			Regex re = new Regex("^" + text + "(?: \\(\\d+\\))?" + text2 + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			return (from p in Directory.EnumerateFiles(path, "*" + Path.GetExtension(name))
				where re.IsMatch(Path.GetFileName(p))
				select p).ToArray();
		}

		private static void Invoke(AutomationElement button)
		{
			InvokePattern invokePattern = ChatUi.Pattern<InvokePattern>(button, InvokePattern.Pattern);
			if (invokePattern == null)
			{
				throw new IOException("GPT 客户端未提供此附件的下载操作。");
			}
			invokePattern.Invoke();
		}

		internal static void Download(Handoff j, ChatUi ui, GptFile link)
		{
			if (j.DownloadStarted == 0)
			{
				if (ui.FileDialog(true) != null)
				{
					throw Retry.Later(new IOException("请先处理 GPT 已经打开的另存为窗口。"));
				}
				j.DownloadName = RelayStore.SafeName(link.Name);
				j.DownloadExisting = Matches(j.DownloadName);
				j.DownloadStarted = DateTime.UtcNow.Ticks;
				j.Detail = ((j.Stage == "await-result") ? "正在下载 Claude 的修改稿。" : "正在从 GPT 下载这份附件。");
				RelayStore.Save(j);
				Invoke(link.Element);
			}
			string text = Path.Combine(RelayStore.Folder(j.Id), (j.Stage == "await-result") ? "result-download" : "download");
			Directory.CreateDirectory(text);
			string text2 = Path.Combine(text, j.DownloadName);
			if (Regular(text2))
			{
				try
				{
					Finish(j, j.DownloadName, text2, null);
					return;
				}
				catch (IOException)
				{
					if (DateTime.UtcNow.Ticks - j.DownloadStarted > TimeSpan.FromMinutes(3.0).Ticks)
					{
						throw;
					}
					return;
				}
			}
			AutomationElement automationElement = ui.FileDialog(true);
			if (automationElement != null)
			{
				ui.SelectFile(automationElement, text2, true);
				return;
			}
			string[] array = (from p in Matches(j.DownloadName).Except(j.DownloadExisting ?? new string[0], StringComparer.OrdinalIgnoreCase)
				where Regular(p) && File.GetLastWriteTimeUtc(p).Ticks >= j.DownloadStarted - TimeSpan.FromSeconds(2.0).Ticks
				select p).ToArray();
			if (array.Length > 1)
			{
				throw new IOException("出现了多个同名下载，后台无法确定应发送哪一个。");
			}
			if (array.Length == 1)
			{
				try
				{
					Finish(j, j.DownloadName, array[0], null);
					return;
				}
				catch (IOException)
				{
					if (DateTime.UtcNow.Ticks - j.DownloadStarted > TimeSpan.FromMinutes(3.0).Ticks)
					{
						throw;
					}
					return;
				}
			}
			if (!j.DownloadPreviewClicked)
			{
				AutomationElement automationElement2 = ChatUi.Of(ui.Root, ControlType.Window).Concat(from e in ChatUi.Of(ui.Root, ControlType.Group)
					where Regex.IsMatch(e.Current.ClassName ?? "", "dialog|file-preview", RegexOptions.IgnoreCase)
					select e).FirstOrDefault((AutomationElement e) => ChatUi.Of(e, ControlType.Text).Any((AutomationElement t) => ChatUi.Name(t) == j.DownloadName));
				if (automationElement2 != null)
				{
					AutomationElement automationElement3 = ChatUi.Of(automationElement2, ControlType.Button).FirstOrDefault((AutomationElement e) => Regex.IsMatch(ChatUi.Name(e), "^(下载|下载文件|下載|下載檔案|Download(?: file)?)$", RegexOptions.IgnoreCase));
					if (automationElement3 != null)
					{
						j.DownloadPreviewClicked = true;
						RelayStore.Save(j);
						Invoke(automationElement3);
					}
				}
			}
			if (DateTime.UtcNow.Ticks - j.DownloadStarted <= TimeSpan.FromMinutes(3.0).Ticks)
			{
				return;
			}
			throw new IOException("GPT 下载尚未完成。下载好同名文件后可在交接记录点继续。");
		}
	}
}
