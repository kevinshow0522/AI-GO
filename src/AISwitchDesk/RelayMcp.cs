using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;

namespace AISwitchDesk
{
	internal static class RelayMcp
	{
		private static Dictionary<string, object> Dict(object o)
		{
			return (o as Dictionary<string, object>) ?? new Dictionary<string, object>();
		}

		private static object Get(Dictionary<string, object> d, string k)
		{
			object value;
			if (!d.TryGetValue(k, out value))
			{
				return null;
			}
			return value;
		}

		private static string Str(Dictionary<string, object> d, string k)
		{
			return Get(d, k) as string;
		}

		private static object Field(string description)
		{
			return new
			{
				type = "string",
				description = description
			};
		}

		private static object Result(string text, bool error = false)
		{
			return new
			{
				content = new[]
				{
					new
					{
						type = "text",
						text = text
					}
				},
				isError = error
			};
		}

		private static string CallingThread(Dictionary<string, object> data)
		{
			Dictionary<string, object> d = Dict(Get(data, "_meta"));
			string[] array = new string[6] { "openai/threadId", "openai/thread_id", "codexThreadId", "codex_thread_id", "threadId", "thread_id" };
			foreach (string k in array)
			{
				string text = ConversationRoute.Codex(Str(d, k));
				if (text != null)
				{
					return text;
				}
			}
			object obj = Get(d, "x-codex-turn-metadata");
			if (obj is string)
			{
				try
				{
					obj = RelayStore.Json.DeserializeObject((string)obj);
				}
				catch (ArgumentException)
				{
					return null;
				}
			}
			return ConversationRoute.Codex(Str(Dict(obj), "thread_id"));
		}

		private static object[] Tools(string from)
		{
			List<object> list = new List<object>();
			list.Add(new
			{
				name = "handoff_file",
				description = "将 UTF-8 文件送到另一方的配对对话。需要对方修改时启用 return_edited 并提供发起本次请求的原话。",
				inputSchema = new
				{
					type = "object",
					properties = new
					{
						filename = Field("文件名及扩展名"),
						content = Field("完整文件正文"),
						instruction = Field("对方应完成的任务"),
						request_key = Field("本次请求的唯一标识，重试复用"),
						new_chat = new
						{
							type = "boolean",
							description = "首次交接时创建新聊天"
						},
						return_edited = new
						{
							type = "boolean",
							description = "让 Claude 将修改稿自动交回原 GPT 对话"
						},
						source_prompt = Field("可选。发起这次交接的完整用户原话，用于定位原 GPT 对话"),
						source_conversation = Field("可选。已知的真实 GPT 聊天链接或当前 Codex 线程 UUID")
					},
					required = new string[4] { "filename", "content", "instruction", "request_key" },
					additionalProperties = false
				}
			});
			if (from == "Claude")
			{
				list.Add(new
				{
					name = "return_file",
					description = "Claude 完成修改后，交回本次 GPT 交接的最终文件。后台自动送回原对话。文本传 content，二进制可传 base64；较大文件请按交接说明在回复中提供下载附件。",
					inputSchema = new
					{
						type = "object",
						properties = new
						{
							id = Field("原交接说明中的完整编号"),
							filename = Field("最终文件名及扩展名"),
							content = Field("完整 UTF-8 正文"),
							base64 = Field("二进制文件的完整 Base64 编码")
						},
						required = new string[2] { "id", "filename" },
						additionalProperties = false
					}
				});
			}
			list.Add(new
			{
				name = "handoff_status",
				description = "查看交接、修改和回传进度。",
				inputSchema = new
				{
					type = "object",
					properties = new
					{
						id = Field("交接编号")
					},
					required = new string[1] { "id" },
					additionalProperties = false
				}
			});
			return list.ToArray();
		}

		// The host owns stdin/stdout for the life of the connection. Change handlers
		// between requests so an installed upgrade does not interrupt a conversation.
		internal static void Run(string from)
		{
			var original = typeof(RelayMcp).Assembly;
			Func<string, string, string> respond = Respond;
			using (var reader = new StreamReader(Console.OpenStandardInput(), RelayStore.Utf8))
			using (var writer = new StreamWriter(Console.OpenStandardOutput(), RelayStore.Utf8) { AutoFlush = true })
			{
				string input;
				while ((input = reader.ReadLine()) != null)
				{
					respond = InstalledHandler(original, respond);
					var reply = respond(from, input);
					if (reply != null) writer.WriteLine(reply);
				}
			}
		}

		private static Func<string, string, string> InstalledHandler(Assembly original, Func<string, string, string> current)
		{
			try
			{
				var installed = AssemblyName.GetAssemblyName(original.Location);
				if (installed.FullName == current.Method.DeclaringType.Assembly.FullName) return current;
				if (installed.FullName == original.FullName) return Respond;
				if (installed.Name != original.GetName().Name || installed.Version < new Version(3, 5, 6, 0)) return current;
				var updated = Assembly.Load(File.ReadAllBytes(original.Location));
				if (updated.FullName != installed.FullName) return current;
				var type = updated.GetType(typeof(RelayMcp).FullName);
				if (type == null) return current;
				var method = type.GetMethod("Respond", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
				if (method != null && method.ReturnType == typeof(string))
					return (Func<string, string, string>)Delegate.CreateDelegate(typeof(Func<string, string, string>), method);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
			catch (BadImageFormatException) { }
			return current;
		}

		internal static string Respond(string from, string input)
		{
			object obj = null;
			try
			{
				Dictionary<string, object> d = Dict(RelayStore.Json.DeserializeObject(input));
				obj = Get(d, "id");
				if (obj == null)
				{
					return null;
				}
				string text = Str(d, "method");
				Dictionary<string, object> dictionary = Dict(Get(d, "params"));
				object result;
				switch (text)
				{
				case "initialize":
				{
					string text10 = Str(dictionary, "protocolVersion");
					if (text10 != "2024-11-05" && text10 != "2025-03-26" && text10 != "2025-06-18" && text10 != "2025-11-25")
					{
						text10 = "2025-06-18";
					}
					result = new
					{
						protocolVersion = text10,
						capabilities = new
						{
							tools = new object()
						},
						serverInfo = new
						{
							name = "ai-duochuang",
							version = typeof(RelayMcp).Assembly.GetName().Version.ToString()
						}
					};
					break;
				}
				case "ping":
					result = new object();
					break;
				case "tools/list":
					result = new
					{
						tools = Tools(from)
					};
					break;
				case "tools/call":
					try
					{
						Dictionary<string, object> dictionary2 = Dict(Get(dictionary, "arguments"));
						string text2 = Str(dictionary, "name");
						Handoff handoff;
						if (text2 == "handoff_file")
						{
							string text3 = Str(dictionary2, "request_key");
							string text4 = Str(dictionary2, "content");
							if (string.IsNullOrWhiteSpace(text3) || text3.Length > 200 || text4 == null)
							{
								throw new ArgumentException("需要正文和有效的交接识别串。");
							}
							byte[] array;
							using (SHA256 sHA = SHA256.Create())
							{
								array = sHA.ComputeHash(RelayStore.Utf8.GetBytes(from + "\n" + text3));
							}
							string fixedId = BitConverter.ToString(array, 0, 16).Replace("-", "").ToLowerInvariant();
							bool flag = from == "ChatGPT";
							string text5 = Str(dictionary2, "source_prompt");
							string text6 = Str(dictionary2, "source_conversation");
							string text7 = (flag ? (CallingThread(dictionary) ?? ConversationRoute.Canonical("ChatGPT", text6) ?? ConversationRoute.Codex(text6)) : null);
							string sourceProof = ConversationRoute.Proof(text5);
							if (flag && text7 == null && text5 != null)
							{
								string proof;
								text7 = ConversationRoute.LocalSession(text5, null, null, out proof);
							}
							bool roundTrip = flag && (dictionary2.ContainsKey("return_edited") ? object.Equals(Get(dictionary2, "return_edited"), true) : RoundTrips.Requested(Str(dictionary2, "instruction")));
							handoff = RelayStore.Create(from, Str(dictionary2, "filename"), Str(dictionary2, "instruction"), null, text4, null, fixedId, object.Equals(Get(dictionary2, "new_chat"), true), true, null, 0L, null, null, flag, roundTrip, text7, sourceProof, text3);
							if (handoff.Stage != "done" && handoff.Stage != "cancel")
							{
								BackgroundRelay.EnsureRunning();
							}
						}
						else if (text2 == "return_file" && from == "Claude")
						{
							string text8 = Str(dictionary2, "content");
							string text9 = Str(dictionary2, "base64");
							if (text8 == null == (text9 == null))
							{
								throw new ArgumentException("请提供正文或 Base64 文件内容中的一项。");
							}
							using (MemoryStream input2 = new MemoryStream((text8 != null) ? RelayStore.Utf8.GetBytes(text8) : Convert.FromBase64String(text9)))
							{
								handoff = RoundTrips.Accept(Str(dictionary2, "id"), Str(dictionary2, "filename"), input2);
							}
							BackgroundRelay.EnsureRunning();
						}
						else
						{
							if (!(text2 == "handoff_status"))
							{
								throw new ArgumentException("未知工具。");
							}
							handoff = RelayStore.Load(Str(dictionary2, "id"));
							if (handoff.From != from && (!handoff.RoundTrip || !(handoff.To == from)))
							{
								throw new ArgumentException("交接编号不属于当前发送方。");
							}
						}
						result = Result(RelayStore.Json.Serialize(new
						{
							id = handoff.Id,
							from = handoff.From,
							to = handoff.To,
							filename = handoff.Name,
							returned_file = handoff.ReturnName,
							status = handoff.StageLabel(),
							detail = handoff.Detail
						}));
					}
					catch (Exception ex)
					{
						result = Result(ex.Message, true);
					}
					break;
				default:
					return RelayStore.Json.Serialize(new
					{
						jsonrpc = "2.0",
						id = obj,
						error = new
						{
							code = -32601,
							message = "Method not found"
						}
					});
				}
				return RelayStore.Json.Serialize(new
				{
					jsonrpc = "2.0",
					id = obj,
					result = result
				});
			}
			catch (Exception)
			{
				return RelayStore.Json.Serialize(new
				{
					jsonrpc = "2.0",
					id = obj,
					error = new
					{
						code = -32600,
						message = "Invalid request"
					}
				});
			}
		}
	}
}
