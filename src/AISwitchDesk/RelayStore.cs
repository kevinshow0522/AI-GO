using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace AISwitchDesk
{
	internal static class RelayStore
	{
		internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Switch Desk");

		internal static readonly string Jobs = Path.Combine(Root, "handoffs");

		internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

		internal static JavaScriptSerializer Json
		{
			get
			{
				JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
				javaScriptSerializer.MaxJsonLength = 4500000;
				return javaScriptSerializer;
			}
		}

		internal static string Other(string name)
		{
			if (name == "ChatGPT")
			{
				return "Claude";
			}
			if (name == "Claude")
			{
				return "ChatGPT";
			}
			throw new ArgumentException("请选择 ChatGPT 或 Claude。");
		}

		internal static string Folder(string id)
		{
			Guid result;
			if (!Guid.TryParseExact(id, "N", out result))
			{
				throw new ArgumentException("无效的交接编号。");
			}
			return Path.Combine(Jobs, result.ToString("N"));
		}

		internal static string FileOf(Handoff j)
		{
			return Path.Combine(Folder(j.Id), "file", SafeName(j.Name));
		}

		internal static string ResultFile(Handoff j)
		{
			return Path.Combine(Folder(j.Id), "result", SafeName(j.ReturnName));
		}

		internal static string SafeName(string value)
		{
			string text = (value ?? "").Trim();
			if (text.Length < 1 || text.Length > 100 || text.EndsWith(".") || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(text, "^(?:\\.{1,2}$|(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\\.|$))", RegexOptions.IgnoreCase))
			{
				throw new ArgumentException("请填写一个有效的文件名。");
			}
			return text;
		}

		internal static void AtomicText(string path, string body)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			string text = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.WriteAllText(text, body, Utf8);
				for (int attempt = 0; ; attempt++)
				{
					try
					{
						if (File.Exists(path)) File.Replace(text, path, null);
						else File.Move(text, path);
						break;
					}
					catch (IOException error)
					{
						if (!File.Exists(text) || !WaitForFileChange(error, attempt)) throw;
					}
				}
			}
			finally
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
		}

		private static bool WaitForFileChange(IOException error, int attempt)
		{
			// Windows readers and file scanners can temporarily deny rename/delete.
			int code = error.HResult & 0xffff;
			if (attempt >= 5 || (code != 5 && code != 32 && code != 33 && code != 80 && code != 183 && code != 1175))
				return false;
			Thread.Sleep(40 << attempt);
			return true;
		}

		internal static Handoff Create(string from, string name, string instruction, string task, string content, string localFile, string fixedId = null, bool newChat = false, bool background = false, string origin = null, long originWindow = 0L, string element = null, string page = null, bool pairChats = false, bool roundTrip = false, string sourceRoute = null, string sourceProof = null, string requestKey = null, string initialStage = null, bool fromFolder = false)
		{
			if (string.IsNullOrWhiteSpace(instruction))
			{
				instruction = "请阅读附件并给出你的意见。";
			}
			if (instruction.Length > 15000 || (task ?? "").Length > 15000 || (content ?? "").Length > 1000000)
			{
				throw new ArgumentException("内容过长。已有文件可以直接选择附件发送。");
			}
			bool flag = !string.IsNullOrWhiteSpace(task);
			if (origin == null && !flag && content == null && localFile == null)
			{
				throw new ArgumentException("需要文件或写作任务。");
			}
			Handoff handoff = new Handoff();
			handoff.Id = fixedId ?? Guid.NewGuid().ToString("N");
			handoff.From = from;
			handoff.To = Other(from);
			handoff.Name = SafeName(name);
			handoff.Instruction = instruction.Trim();
			handoff.Task = task;
			handoff.Stage = initialStage ?? ((origin != null) ? "capture" : (flag ? "ask" : "ready"));
			handoff.NewChat = newChat;
			handoff.Background = background;
			handoff.Origin = origin;
			handoff.OriginWindow = originWindow;
			handoff.OriginElement = element;
			handoff.SourcePage = page;
			handoff.RequestedName = ((origin == null) ? null : name);
			handoff.Created = DateTime.UtcNow.Ticks;
			handoff.PairChats = pairChats;
			handoff.RoundTrip = roundTrip;
			handoff.SourceRoute = sourceRoute;
			handoff.SourceProof = sourceProof;
			handoff.RequestKey = requestKey;
			handoff.FromFolder = fromFolder;
			Handoff handoff2 = handoff;
			handoff2.Updated = handoff2.Created;
			Directory.CreateDirectory(Jobs);
			string text = Folder(handoff2.Id);
			if (Directory.Exists(text))
			{
				return Existing(handoff2, content);
			}
			string text2 = Path.Combine(Jobs, ".new-" + Guid.NewGuid().ToString("N"));
			string text3 = Path.Combine(text2, "file");
			Directory.CreateDirectory(text3);
			try
			{
				if (localFile != null)
				{
					File.Copy(localFile, Path.Combine(text3, handoff2.Name), false);
				}
				else if (content != null)
				{
					File.WriteAllText(Path.Combine(text3, handoff2.Name), content, Utf8);
				}
				File.WriteAllText(Path.Combine(text2, "state.json"), Json.Serialize(handoff2), Utf8);
				for (int attempt = 0; ; attempt++)
				{
					try
					{
						Directory.Move(text2, text);
						return handoff2;
					}
					catch (IOException error)
					{
						if (Directory.Exists(text)) return Existing(handoff2, content);
						if (!WaitForFileChange(error, attempt)) throw;
					}
				}
			}
			finally
			{
				if (Directory.Exists(text2))
				{
					Directory.Delete(text2, true);
				}
			}
		}

		private static Handoff Existing(Handoff expected, string content)
		{
			Handoff handoff = Load(expected.Id);
			if (handoff.RoundTrip != expected.RoundTrip)
			{
				throw new InvalidOperationException("这个交接编号已用于不同的回传设置，请使用新的请求编号。");
			}
			if (handoff.From != expected.From || handoff.Name != expected.Name || handoff.Instruction != expected.Instruction || handoff.NewChat != expected.NewChat || (expected.SourceRoute != null && handoff.SourceRoute != null && expected.SourceRoute != handoff.SourceRoute) || (expected.SourceProof != null && handoff.SourceProof != null && expected.SourceProof != handoff.SourceProof) || content == null || !File.Exists(FileOf(handoff)) || File.ReadAllText(FileOf(handoff)) != content)
			{
				throw new InvalidOperationException("这个交接编号已用于其他文件。");
			}
			return handoff;
		}

		internal static Handoff Load(string id)
		{
			using (var file = new FileStream(Path.Combine(Folder(id), "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
			using (var reader = new StreamReader(file, Utf8))
				return Json.Deserialize<Handoff>(reader.ReadToEnd());
		}

		internal static List<Handoff> All()
		{
			List<Handoff> list = new List<Handoff>();
			if (!Directory.Exists(Jobs))
			{
				return list;
			}
			foreach (string item in Directory.EnumerateDirectories(Jobs))
			{
				string fileName = Path.GetFileName(item);
				Guid result;
				if (!Guid.TryParseExact(fileName, "N", out result))
				{
					continue;
				}
				try
				{
					Handoff handoff = Load(fileName);
					if (handoff != null && handoff.Id == fileName)
					{
						list.Add(handoff);
					}
				}
				catch (IOException)
				{
				}
				catch (ArgumentException)
				{
				}
				catch (InvalidOperationException)
				{
				}
			}
			return list.OrderByDescending((Handoff x) => x.Created).ToList();
		}

		internal static void Save(Handoff job)
		{
			using (Mutex mutex = new Mutex(false, "Local\\AISwitchDesk.Relay." + job.Id))
			{
				try
				{
					if (!mutex.WaitOne(2000))
					{
						throw new IOException("交接文件正在更新。");
					}
				}
				catch (AbandonedMutexException)
				{
				}
				try
				{
					if (Load(job.Id).Revision != job.Revision)
					{
						throw new InvalidOperationException("交接状态已更新。");
					}
					long previousUpdate = job.Updated;
					job.Revision++;
					job.Updated = DateTime.UtcNow.Ticks;
					try { AtomicText(Path.Combine(Folder(job.Id), "state.json"), Json.Serialize(job)); }
					catch
					{
						job.Revision--;
						job.Updated = previousUpdate;
						throw;
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
