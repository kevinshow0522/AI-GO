using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AISwitchDesk
{
	internal sealed class Outbox
	{
		internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "AI 双窗投递");

		private readonly Dictionary<string, string> observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private readonly HashSet<string> completed = new HashSet<string>(StringComparer.Ordinal);

		private static readonly HashSet<string> Formats = new HashSet<string>(".txt .md .csv .json .pdf .docx .xlsx .html .odt .rtf .epub .jpg .jpeg .png .gif .webp .py .js .ts .cs .xml .yaml .yml".Split(' '), StringComparer.OrdinalIgnoreCase);

		internal static string To(string target)
		{
			return Path.Combine(Root, "发给 " + target);
		}

		internal static void Prepare()
		{
			Directory.CreateDirectory(To("Claude"));
			Directory.CreateDirectory(To("ChatGPT"));
		}

		private static bool Link(string path)
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}

		internal void Scan()
		{
			Prepare();
			if (Link(Root))
			{
				return;
			}
			string[] array = new string[2] { "Claude", "ChatGPT" };
			foreach (string target in array)
			{
				string path = To(target);
				if (Link(path))
				{
					continue;
				}
				foreach (string item in Directory.EnumerateFiles(path))
				{
					if (Formats.Contains(Path.GetExtension(item)) && !item.EndsWith(".任务.txt", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(item).StartsWith("~$"))
					{
						try
						{
							Inspect(item, target);
						}
						catch (IOException)
						{
						}
						catch (UnauthorizedAccessException)
						{
						}
						catch (ArgumentException)
						{
						}
					}
				}
			}
		}

		private void Inspect(string path, string target)
		{
			FileInfo fileInfo = new FileInfo(path);
			if (Link(path) || (fileInfo.Attributes & (FileAttributes.Hidden | FileAttributes.Temporary)) != 0)
			{
				return;
			}
			string text = path + "\n" + fileInfo.Length + "\n" + fileInfo.LastWriteTimeUtc.Ticks + "\n" + fileInfo.CreationTimeUtc.Ticks;
			if (completed.Contains(text))
			{
				return;
			}
			string value;
			if (!observed.TryGetValue(path, out value) || value != text)
			{
				observed[path] = text;
				return;
			}
			string text2;
			using (SHA256 sHA = SHA256.Create())
			{
				text2 = BitConverter.ToString(sHA.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 16).Replace("-", "").ToLowerInvariant();
			}
			if (File.Exists(Path.Combine(RelayStore.Folder(text2), "state.json")))
			{
				completed.Add(text);
				return;
			}
			string text3 = "请阅读附件并给出你的意见。";
			string text4 = path + ".任务.txt";
			if (File.Exists(text4))
			{
				if (Link(text4) || new FileInfo(text4).Length > 32000)
				{
					return;
				}
				text3 = File.ReadAllText(text4);
				if (text3.Length > 15000)
				{
					return;
				}
			}
			using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				if (fileStream.Length != fileInfo.Length || File.GetLastWriteTimeUtc(path) != fileInfo.LastWriteTimeUtc)
				{
					return;
				}
				RelayStore.Create(RelayStore.Other(target), fileInfo.Name, text3, null, null, path, text2, true, true, null, 0L, null, null, false, false, null, null, null, null, true);
			}
			completed.Add(text);
		}
	}
}
