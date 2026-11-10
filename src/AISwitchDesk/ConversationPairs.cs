using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AISwitchDesk
{
	internal static class ConversationPairs
	{
		private static string FileName
		{
			get
			{
				return Path.Combine(RelayStore.Root, "conversation-pairs.json");
			}
		}

		private static Dictionary<string, string> Read()
		{
			if (!File.Exists(FileName))
			{
				return new Dictionary<string, string>();
			}
			return RelayStore.Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(FileName));
		}

		internal static string Find(string source)
		{
			return ConversationRoute.Canonical("Claude", Snapshot(source));
		}

		internal static string Snapshot(string source)
		{
			string value;
			if (source == null || !Read().TryGetValue(source, out value))
			{
				return "";
			}
			return value;
		}

		internal static string Command(string text)
		{
			string input = (text ?? "").Trim().TrimEnd('。', '！', '!', '.');
			if (Regex.IsMatch(input, "^(?:请|请帮我|帮我)?(?:解除|取消)(?:当前|这条|本次)?(?:\\s*GPT\\s*)?(?:对话)?(?:的)?配对$", RegexOptions.IgnoreCase))
			{
				return "unpair";
			}
			if (Regex.IsMatch(input, "^(?:请|请帮我|帮我)?(?:配对|绑定)(?:当前|这条|这两个)?\\s*(?:GPT|ChatGPT)\\s*(?:对话)?\\s*(?:和|与|、)\\s*(?:当前)?\\s*Claude\\s*(?:对话)?$|^(?:请)?把(?:当前)?\\s*(?:GPT|ChatGPT)\\s*(?:对话)?\\s*(?:和|与)\\s*(?:当前)?\\s*Claude\\s*(?:对话)?(?:配对|绑定)$", RegexOptions.IgnoreCase))
			{
				return "pair";
			}
			return null;
		}

		internal static void ApplyCommand(Handoff job, string command)
		{
			string[] array = (from h in BackgroundRelay.Windows("Claude")
				select ConversationRoute.Canonical("Claude", new ChatUi(h, () => false).PageKey()) into s
				where s != null
				select s).Distinct().ToArray();
			if (job.SourceRoute == null || (command == "pair" && array.Length != 1))
			{
				job.Stage = "pair-missing";
				job.Detail = "请打开要配对的 GPT 对话和一个已有的 Claude 对话，再发送配对指令。";
			}
			else
			{
				string text = ((command == "pair") ? array[0] : null);
				Set(job.SourceRoute, text, true);
				job.Stage = ((text != null) ? "paired" : "unpaired");
				job.ClaudeRoute = text;
				job.Detail = ((text != null) ? "之后从这条 GPT 对话交出的文件将送到选定的 Claude 对话。" : "之后的新交接会重新配对。");
			}
			RelayStore.Save(job);
		}

		internal static void Set(string source, string target, bool replace, string observed = null)
		{
			if (ConversationRoute.Canonical("ChatGPT", source) == null || (target != null && ConversationRoute.Canonical("Claude", target) == null))
			{
				throw new ArgumentException("请先打开需要配对的两条具体对话。");
			}
			using (Mutex mutex = new Mutex(false, "Local\\AISwitchDesk.ConversationPairs.v1"))
			{
				try
				{
					if (!mutex.WaitOne(5000))
					{
						throw new IOException("对话配对正在更新。");
					}
				}
				catch (AbandonedMutexException)
				{
				}
				try
				{
					Dictionary<string, string> dictionary = Read();
					string value;
					if ((dictionary.TryGetValue(source, out value) && value == target) || (!replace && ((observed != null && observed != (value ?? "")) || ConversationRoute.Canonical("Claude", value) != null || dictionary.ContainsValue(target))))
					{
						return;
					}
					string value2 = (dictionary[source] = "unpaired-" + Guid.NewGuid().ToString("N"));
					if (target != null)
					{
						string[] array = (from p in dictionary
							where p.Value == target
							select p.Key).ToArray();
						foreach (string key in array)
						{
							dictionary[key] = value2;
						}
						dictionary[source] = target;
					}
					RelayStore.AtomicText(FileName, RelayStore.Json.Serialize(dictionary));
				}
				finally
				{
					mutex.ReleaseMutex();
				}
			}
		}
	}
}
