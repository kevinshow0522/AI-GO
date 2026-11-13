using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal static class ConversationRoute
	{
		private static readonly object recentLock = new object();

		private static string recentKey;

		private static string recentRoute;

		private static DateTime recentAt;

		internal static string Codex(string id)
		{
			Guid result;
			if (!Guid.TryParseExact(id, "D", out result))
			{
				return null;
			}
			return "codex://threads/" + result.ToString("D");
		}

		internal static string Canonical(string app, string raw)
		{
			Uri result;
			if (!Uri.TryCreate(raw, UriKind.Absolute, out result) || result.UserInfo.Length != 0 || !result.IsDefaultPort)
			{
				return null;
			}
			string[] array = result.AbsolutePath.Trim('/').Split('/');
			Guid result2;
			if (!Guid.TryParseExact(array.Last(), "D", out result2))
			{
				return null;
			}
			if (app == "Claude" && result.Scheme == "https" && result.Host == "claude.ai" && array.Length == 2 && array[0] == "chat")
			{
				return "https://claude.ai/chat/" + result2.ToString("D");
			}
			if (app != "ChatGPT")
			{
				return null;
			}
			if (result.Scheme == "https" && result.Host == "chatgpt.com" && array.Length >= 2 && array[array.Length - 2] == "c")
			{
				return "https://chatgpt.com/c/" + result2.ToString("D");
			}
			if (result.Scheme == "codex" && result.Host == "threads" && array.Length == 1)
			{
				return Codex(result2.ToString("D"));
			}
			if (result.Scheme == "app" && array.Length == 2 && (array[0] == "threads" || array[0] == "local"))
			{
				return Codex(result2.ToString("D"));
			}
			return null;
		}

		internal static string Proof(string prompt)
		{
			if (!string.IsNullOrWhiteSpace(prompt))
			{
				return RelayIntent.Fingerprint(Regex.Replace(prompt, "\\s+", " ").Trim());
			}
			return null;
		}

		internal static bool LocalReceipt(Handoff job)
		{
			if (job.Stage != Stages.ReturnConfirm || job.SourceRoute == null || !job.SourceRoute.StartsWith("codex://threads/") || job.ReturnName == null)
			{
				return false;
			}
			string text = new Uri(job.SourceRoute).AbsolutePath.Trim('/');
			if (Codex(text) != job.SourceRoute)
			{
				return false;
			}
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex\\sessions");
			if (!Directory.Exists(path))
			{
				return false;
			}
			foreach (string item in Directory.EnumerateFiles(path, "rollout-*-" + text + ".jsonl", SearchOption.AllDirectories))
			{
				using (FileStream fileStream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					bool flag = fileStream.Length > 6291456;
					if (flag)
					{
						fileStream.Seek(-6291456L, SeekOrigin.End);
					}
					using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8))
					{
						if (flag)
						{
							streamReader.ReadLine();
						}
						string text2;
						while ((text2 = streamReader.ReadLine()) != null)
						{
							if (text2.Length > 4400000 || !text2.Contains(job.Id) || !text2.Contains("\"role\":\"user\""))
							{
								continue;
							}
							try
							{
								Dictionary<string, object> dictionary = RelayStore.Json.Deserialize<Dictionary<string, object>>(text2);
								object value;
								if (!dictionary.TryGetValue("payload", out value))
								{
									continue;
								}
								Dictionary<string, object> dictionary2 = value as Dictionary<string, object>;
								object value2;
								object value3;
								if (dictionary2 == null || !dictionary2.TryGetValue("role", out value2) || !object.Equals(value2, "user") || !dictionary2.TryGetValue("content", out value3))
								{
									continue;
								}
								IEnumerable enumerable = value3 as IEnumerable;
								if (enumerable != null)
								{
									string text3 = string.Join("\n", from x in enumerable.Cast<object>().OfType<Dictionary<string, object>>()
										where x.ContainsKey("text")
										select x["text"] as string);
									if (text3.Contains(job.Receipt) && text3.Contains(job.ReturnName))
									{
										return true;
									}
								}
							}
							catch (ArgumentException)
							{
							}
							catch (InvalidOperationException)
							{
							}
						}
					}
				}
			}
			return false;
		}

		internal static bool Matches(ChatUi ui, string route, string proof)
		{
			if (route == null)
			{
				return false;
			}
			string app = (route.StartsWith("https://claude.ai/") ? "Claude" : "ChatGPT");
			string text = ui.PageKey();
			string text2 = Canonical(app, text);
			if (text2 != null)
			{
				return text2 == route;
			}
			if (!route.StartsWith("codex://threads/") || !text.StartsWith("app://") || proof == null)
			{
				return false;
			}
			GptTranscript gptTranscript = GptTranscript.Read(ui.Window);
			if (!gptTranscript.Codex || gptTranscript.Turns.Count == 0)
			{
				return false;
			}
			GptTurn gptTurn = gptTranscript.Turns.FirstOrDefault((GptTurn t) => Proof(t.Prompt) == proof);
			GptTurn gptTurn2 = gptTranscript.Turns.Last();
			string text3 = ui.Window.ToInt64() + "|" + route + "|" + gptTurn2.Key + "|" + proof;
			lock (recentLock)
			{
				if (text3 == recentKey && DateTime.UtcNow - recentAt < TimeSpan.FromSeconds(3.0))
				{
					return recentRoute == route;
				}
				string proof2;
				string text4 = LocalSession(gptTurn2.Prompt, null, null, out proof2, route);
				if (text4 == null && gptTurn != null && gptTurn != gptTurn2)
				{
					text4 = LocalSession(gptTurn.Prompt, null, null, out proof2, route);
				}
				recentKey = text3;
				recentRoute = text4;
				recentAt = DateTime.UtcNow;
				return text4 == route;
			}
		}

		internal static void Navigate(ChatUi ui, string route)
		{
			if (ui.HasDraftOrAttachment())
			{
				throw new ConversationPending("正在等候当前草稿发出或清空，再定位配对对话。");
			}
			string app = (route.StartsWith("https://claude.ai/") ? "Claude" : "ChatGPT");
			AutomationElement automationElement = ChatUi.Of(ui.Root, ControlType.Hyperlink).FirstOrDefault(delegate(AutomationElement e)
			{
				ValuePattern valuePattern = ChatUi.Pattern<ValuePattern>(e, ValuePattern.Pattern);
				return valuePattern != null && Canonical(app, valuePattern.Current.Value) == route;
			});
			if (automationElement != null)
			{
				InvokePattern invokePattern = ChatUi.Pattern<InvokePattern>(automationElement, InvokePattern.Pattern);
				if (invokePattern != null)
				{
					invokePattern.Invoke();
					return;
				}
			}
			string text = (route.StartsWith("codex://threads/") ? route : ((app == "Claude") ? ("claude://claude.ai" + new Uri(route).AbsolutePath) : null));
			if (text == null)
			{
				throw new ConversationPending("请在 GPT 客户端打开原对话，回传会自动继续。");
			}
			if (route.StartsWith("codex://threads/"))
			{
				string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex\\session_index.jsonl");
				string text2 = new Uri(route).AbsolutePath.Trim('/');
				string title = null;
				if (File.Exists(path))
				{
					using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
					{
						using (StreamReader streamReader = new StreamReader(stream, Encoding.UTF8))
						{
							string text3;
							while ((text3 = streamReader.ReadLine()) != null)
							{
								if (!text3.Contains(text2))
								{
									continue;
								}
								try
								{
									Dictionary<string, object> dictionary = RelayStore.Json.Deserialize<Dictionary<string, object>>(text3);
									if (dictionary.ContainsKey("id") && object.Equals(dictionary["id"], text2) && dictionary.ContainsKey("thread_name"))
									{
										title = dictionary["thread_name"] as string;
									}
								}
								catch (ArgumentException)
								{
								}
							}
						}
					}
				}
				if (!string.IsNullOrWhiteSpace(title))
				{
					AutomationElement[] array = (from e in ChatUi.Of(ui.Root, ControlType.Button)
						where e.Current.ClassName.Contains("sidebar-item") && ChatUi.Name(e) == title
						select e).ToArray();
					if (array.Length == 1)
					{
						InvokePattern invokePattern2 = ChatUi.Pattern<InvokePattern>(array[0], InvokePattern.Pattern);
						if (invokePattern2 != null)
						{
							invokePattern2.Invoke();
							return;
						}
					}
				}
				string text4 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\WindowsApps\\chatgpt-desktop.exe");
				if (File.Exists(text4))
				{
					ProcessStartInfo processStartInfo = new ProcessStartInfo(text4, text);
					processStartInfo.UseShellExecute = false;
					processStartInfo.CreateNoWindow = true;
					Process.Start(processStartInfo);
					return;
				}
			}
			ProcessStartInfo processStartInfo2 = new ProcessStartInfo(text);
			processStartInfo2.UseShellExecute = true;
			Process.Start(processStartInfo2);
		}

		internal static string LocalSession(string prompt, string key, string preferred, out string proof, string alsoCheck = null)
		{
			proof = null;
			string path2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex\\sessions");
			if (!Directory.Exists(path2) || (string.IsNullOrEmpty(prompt) && string.IsNullOrEmpty(key)))
			{
				return null;
			}
			string text = Proof(prompt);
			string text2 = null;
			string text3 = null;
			DateTime since = DateTime.UtcNow.AddMinutes(-40.0);
			string text4 = preferred ?? alsoCheck;
			string suffix = ((text4 != null && text4.StartsWith("codex://threads/")) ? (new Uri(text4).AbsolutePath.Trim('/') + ".jsonl") : null);
			FileInfo[] array = (from path in Directory.EnumerateFiles(path2, (preferred != null && suffix != null) ? ("rollout-*-" + suffix) : "rollout-*.jsonl", SearchOption.AllDirectories)
				select new FileInfo(path) into f
				where f.LastWriteTimeUtc >= since || (suffix != null && f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				orderby f.LastWriteTimeUtc descending
				select f).ToArray();
			FileInfo[] array2 = array;
			foreach (FileInfo fileInfo in array2)
			{
				Match match = Regex.Match(fileInfo.Name, "([0-9a-fA-F-]{36})\\.jsonl$");
				string text5 = (match.Success ? Codex(match.Groups[1].Value) : null);
				if (text5 == null || (preferred != null && text5 != preferred))
				{
					continue;
				}
				string text6 = null;
				string text7 = null;
				using (FileStream fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					bool flag = fileStream.Length > 8388608;
					if (flag)
					{
						fileStream.Seek(-8388608L, SeekOrigin.End);
					}
					using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8))
					{
						if (flag)
						{
							streamReader.ReadLine();
						}
						string text8;
						while ((text8 = streamReader.ReadLine()) != null)
						{
							if (text8.Length > 4400000)
							{
								continue;
							}
							bool flag2 = text8.Contains("\"user_message\"") || text8.Contains("\"role\":\"user\"") || text8.Contains("<codex_delegation>");
							bool flag3 = key != null && text8.Contains(key) && (text8.Contains("handoff_file") || (text8.Contains("filename") && text8.Contains("status") && text8.Contains("ChatGPT") && text8.Contains("detail")));
							if (!flag2 && !flag3)
							{
								continue;
							}
							try
							{
								Dictionary<string, object> dictionary = RelayStore.Json.DeserializeObject(text8) as Dictionary<string, object>;
								object value;
								if (dictionary == null || !dictionary.TryGetValue("payload", out value))
								{
									continue;
								}
								Dictionary<string, object> dictionary2 = value as Dictionary<string, object>;
								if (dictionary2 == null || !dictionary2.TryGetValue("type", out value))
								{
									continue;
								}
								string text9 = value as string;
								string text10 = null;
								if (text9 == "user_message" && dictionary2.TryGetValue("message", out value))
								{
									text10 = value as string;
								}
								else if (text9 == "function_call_output" && dictionary2.TryGetValue("name", out value) && object.Equals(value, "create_thread") && dictionary2.TryGetValue("output", out value) && value is string)
								{
									Match match2 = Regex.Match((string)value, "^\\s*<codex_delegation>\\s*<source_thread_id>[a-f0-9-]+</source_thread_id>\\s*<input>([\\s\\S]*?)</input>");
									if (match2.Success)
									{
										text10 = WebUtility.HtmlDecode(match2.Groups[1].Value);
									}
								}
								else if (text9 == "message" && dictionary2.TryGetValue("role", out value) && object.Equals(value, "user") && dictionary2.TryGetValue("content", out value) && value is object[])
								{
									text10 = string.Join("\n", from part in ((object[])value).OfType<Dictionary<string, object>>()
										where part.ContainsKey("text") && part.ContainsKey("type") && object.Equals(part["type"], "input_text")
										select part["text"] as string);
									if (text10.StartsWith("<environment_context>") || text10.StartsWith("<send_user_message_"))
									{
										text10 = null;
									}
								}
								if (text10 != null)
								{
									text6 = Proof(text10);
									if (text != null && text == text6)
									{
										text7 = text6;
									}
								}
								else
								{
									if (!flag3)
									{
										continue;
									}
									switch (text9)
									{
									case "function_call":
									case "custom_tool_call":
									case "function_call_output":
									case "custom_tool_call_output":
										if (text6 != null)
										{
											text7 = text6;
										}
										break;
									}
									continue;
								}
							}
							catch (ArgumentException)
							{
							}
							catch (InvalidOperationException)
							{
							}
						}
					}
				}
				if (text7 != null)
				{
					if (text2 != null && text2 != text5)
					{
						proof = null;
						return null;
					}
					text2 = text5;
					text3 = text7;
				}
			}
			proof = text3;
			return text2;
		}
	}
}
