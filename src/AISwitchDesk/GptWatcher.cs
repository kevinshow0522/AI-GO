using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal sealed class GptWatcher
	{
		private sealed class WindowState
		{
			internal bool Initialized;

			internal string[] Prior = new string[0];

			internal string Draft;

			internal DateTime SawDraft;

			internal readonly HashSet<string> Tracking = new HashSet<string>();

			internal AutomationElement Editor;

			internal AutomationPropertyChangedEventHandler OnEdit;

			internal void Bind(AutomationElement next)
			{
				if (next == null)
				{
					return;
				}
				if (Editor != null)
				{
					try
					{
						if (Automation.Compare(Editor, next))
						{
							return;
						}
					}
					catch (ElementNotAvailableException)
					{
					}
				}
				Unbind();
				Editor = next;
				OnEdit = delegate(object sender, AutomationPropertyChangedEventArgs change)
				{
					string text = change.NewValue as string;
					if (RelayIntent.Parse(text) != null || ConversationPairs.Command(text) != null)
					{
						lock (this)
						{
							Draft = text;
							SawDraft = DateTime.UtcNow;
						}
					}
				};
				Automation.AddAutomationPropertyChangedEventHandler(Editor, TreeScope.Element, OnEdit, ValuePattern.ValueProperty);
			}

			internal void Unbind()
			{
				if (Editor != null && OnEdit != null)
				{
					try
					{
						Automation.RemoveAutomationPropertyChangedEventHandler(Editor, OnEdit);
					}
					catch (ElementNotAvailableException)
					{
					}
					catch (InvalidOperationException)
					{
					}
				}
				Editor = null;
				OnEdit = null;
			}
		}

		private readonly Dictionary<IntPtr, WindowState> windows = new Dictionary<IntPtr, WindowState>();

		private readonly Dictionary<string, Tuple<string, DateTime>> stable = new Dictionary<string, Tuple<string, DateTime>>();

		internal void Forget()
		{
			foreach (WindowState value in windows.Values)
			{
				value.Unbind();
			}
			windows.Clear();
			stable.Clear();
		}

		internal void Keep(IList<IntPtr> handles)
		{
			IntPtr[] array = windows.Keys.Where((IntPtr h) => !handles.Contains(h)).ToArray();
			foreach (IntPtr key in array)
			{
				windows[key].Unbind();
				windows.Remove(key);
			}
		}

		internal void Poll(IntPtr handle, List<Handoff> jobs)
		{
			Handoff[] array = jobs.Where((Handoff j) => j.Stage == Stages.Capture && !j.Held && j.DownloadStarted > 0 && j.OriginWindow == handle.ToInt64()).ToArray();
			foreach (Handoff j2 in array)
			{
				try
				{
					ArtifactCapture.Download(j2, new ChatUi(handle, () => BackgroundRelay.IsClient(handle, "ChatGPT")), null);
				}
				catch (IOException ex)
				{
					BackgroundRelay.Hold(j2, ex.Message);
				}
			}
			GptTranscript view = GptTranscript.Read(handle);
			if (!view.HasEditor && !view.Turns.Any())
			{
				return;
			}
			WindowState state;
			if (!windows.TryGetValue(handle, out state))
			{
				windows[handle] = (state = new WindowState());
			}
			try
			{
				state.Bind(view.Composer);
			}
			catch (ElementNotAvailableException)
			{
			}
			catch (InvalidOperationException)
			{
			}
			GptTurn last = view.Turns.LastOrDefault();
			string[] keys = view.Turns.Select((GptTurn t) => t.Key).ToArray();
			state.Tracking.RemoveWhere((string id) => !jobs.Any((Handoff j) => j.Id == id && keys.Contains(j.Origin)));
			if (state.Initialized && last != null && !state.Prior.Contains(last.Key))
			{
				RelayIntent relayIntent = RelayIntent.Parse(last.Prompt);
				string text = ConversationPairs.Command(last.Prompt);
				if (relayIntent != null || text != null)
				{
					bool flag;
					lock (state)
					{
						flag = state.Draft != null && DateTime.UtcNow - state.SawDraft < TimeSpan.FromMinutes(4.0) && Normalize(last.Prompt).Contains(Normalize(state.Draft));
					}
					bool flag2 = state.Prior.Length > 0 && keys.Length == state.Prior.Length + 1 && state.Prior.SequenceEqual(keys.Take(state.Prior.Length));
					bool flag3 = state.Prior.Length == 0 && keys.Length == 1 && (view.Streaming || !last.Assistant);
					if ((flag || flag2 || flag3) && !jobs.Any((Handoff j) => j.Origin == last.Key && Belongs(j, handle, last, view, state)))
					{
						string sourceProof = ConversationRoute.Proof(last.Prompt);
						string text2 = ConversationRoute.Canonical("ChatGPT", view.Page);
						if (text2 == null && view.Codex)
						{
							string proof;
							text2 = ConversationRoute.LocalSession(last.Prompt, null, null, out proof);
						}
						Handoff handoff = RelayStore.Create("ChatGPT", (relayIntent == null) ? "对话配对" : (relayIntent.Name ?? "交接.md"), (relayIntent == null) ? text : relayIntent.Instruction, last.Prompt, null, null, null, true, true, last.Key, handle.ToInt64(), last.ElementId, view.Page, true, relayIntent != null && RoundTrips.Requested(last.Prompt), text2, sourceProof, null, (relayIntent == null) ? Stages.PairMissing : null);
						if (text != null)
						{
							ConversationPairs.ApplyCommand(handoff, text);
						}
						state.Tracking.Add(handoff.Id);
						jobs.Add(handoff);
						lock (state)
						{
							state.Draft = null;
						}
					}
				}
			}
			state.Initialized = true;
			state.Prior = keys;
			if (RelayIntent.Parse(view.Draft) != null || ConversationPairs.Command(view.Draft) != null)
			{
				lock (state)
				{
					state.Draft = view.Draft;
					state.SawDraft = DateTime.UtcNow;
				}
			}
			Handoff[] array2 = jobs.Where((Handoff x) => x.Stage == Stages.Capture && !x.Held && x.DownloadStarted == 0).ToArray();
			foreach (Handoff i in array2)
			{
				List<GptTurn> turns = view.Turns;
				Func<GptTurn, bool> predicate = (GptTurn t) => t.Key == i.Origin;
				GptTurn turn = turns.FirstOrDefault(predicate);
				if (turn == null || !Belongs(i, handle, turn, view, state))
				{
					continue;
				}
				state.Tracking.Add(i.Id);
				try
				{
					ClientInput.TryUse("ChatGPT", delegate
					{
						Capture(i, turn, view, handle);
					});
				}
				catch (IOException ex4)
				{
					BackgroundRelay.Hold(i, ex4.Message);
				}
				catch (ArgumentException ex5)
				{
					BackgroundRelay.Hold(i, ex5.Message);
				}
				if (i.Stage != Stages.Capture)
				{
					stable.Remove(i.Id);
				}
			}
		}

		private static bool Belongs(Handoff job, IntPtr handle, GptTurn turn, GptTranscript view, WindowState state)
		{
			if (job.SourceRoute != null)
			{
				string text = ConversationRoute.Canonical("ChatGPT", view.Page);
				if (text != null)
				{
					return text == job.SourceRoute;
				}
				if (job.SourceRoute.StartsWith("codex://") && job.OriginWindow != handle.ToInt64())
				{
					return ConversationRoute.Matches(new ChatUi(handle, () => false), job.SourceRoute, job.SourceProof);
				}
			}
			if (job.SourcePage != null && job.SourcePage.StartsWith("https://chatgpt.com/c/", StringComparison.OrdinalIgnoreCase))
			{
				return job.SourcePage == view.Page;
			}
			if (job.OriginWindow == handle.ToInt64())
			{
				if (!(turn.ElementId == job.OriginElement))
				{
					return state.Tracking.Contains(job.Id);
				}
				return true;
			}
			return false;
		}

		private static string Normalize(string text)
		{
			return Regex.Replace(text ?? "", "\\s+", " ").Trim();
		}

		private bool Settled(string id, string value, double seconds)
		{
			string text = RelayIntent.Fingerprint(value);
			Tuple<string, DateTime> value2;
			if (!stable.TryGetValue(id, out value2) || value2.Item1 != text)
			{
				stable[id] = Tuple.Create(text, DateTime.UtcNow);
				return false;
			}
			return (DateTime.UtcNow - value2.Item2).TotalSeconds >= seconds;
		}

		private static GptFile Pick(IEnumerable<GptFile> items, string name)
		{
			GptFile[] array = (from g in items.GroupBy((GptFile f) => f.Name, StringComparer.OrdinalIgnoreCase)
				select g.First()).ToArray();
			if (name != null)
			{
				GptFile gptFile = array.FirstOrDefault((GptFile f) => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
				if (gptFile != null)
				{
					return gptFile;
				}
				return null;
			}
			if (array.Length > 1)
			{
				throw new IOException("这条回复里有多个附件。请在 GPT 中说出要发给 Claude 的文件名。");
			}
			return array.FirstOrDefault();
		}

		private void Capture(Handoff j, GptTurn turn, GptTranscript view, IntPtr handle)
		{
			RelayIntent intent = RelayIntent.Parse(j.Task);
			if (intent == null)
			{
				return;
			}
			if (turn.ToolHandoff)
			{
				Handoff[] array = (from other in RelayStore.All()
					where other.Id != j.Id && other.From == "ChatGPT" && other.Task == null && other.Created >= j.Created - TimeSpan.FromSeconds(3.0).Ticks && other.Created <= j.Created + TimeSpan.FromMinutes(30.0).Ticks && (turn.Body.Contains(other.Id) || (j.SourceRoute != null && other.SourceRoute == j.SourceRoute && intent.Name != null && string.Equals(intent.Name, other.Name, StringComparison.OrdinalIgnoreCase)))
					select other).ToArray();
				if (array.Length == 1)
				{
					Handoff handoff = array[0];
					if (j.SourceRoute != null && handoff.Stage != Stages.Done && handoff.Stage != Stages.Cancel && (handoff.SourceRoute == null || handoff.SourceProof == null))
					{
						handoff.SourceRoute = j.SourceRoute;
						handoff.SourceProof = j.SourceProof;
						handoff.PairChats = true;
						handoff.RoundTrip |= j.RoundTrip;
						RelayStore.Save(handoff);
					}
					j.LinkedId = handoff.Id;
					j.Stage = Stages.Linked;
					j.Detail = "已由 GPT 提交，跟随同一份文件的接力进度。";
					RelayStore.Save(j);
					return;
				}
				if (array.Length > 1)
				{
					throw new IOException("GPT 已提交多个交接，后台已暂停额外发送。");
				}
			}
			if (intent.LocalPath != null)
			{
				ArtifactCapture.Local(j, intent.LocalPath, true);
				return;
			}
			ChatUi chatUi = new ChatUi(handle, () => BackgroundRelay.IsClient(handle, "ChatGPT"));
			if (j.DownloadStarted > 0)
			{
				ArtifactCapture.Download(j, chatUi, null);
				return;
			}
			if (j.WriterFollowup)
			{
				GptTurn gptTurn = view.Turns.SkipWhile((GptTurn t) => t != turn).Skip(1).FirstOrDefault((GptTurn t) => t.Prompt.Contains("双窗取件 " + j.Id));
				if (gptTurn == null || (gptTurn == view.Turns.Last() && view.Streaming))
				{
					return;
				}
				GptFile gptFile = Pick(gptTurn.Files, intent.Name);
				if (gptFile != null)
				{
					if (gptFile.LocalPath == null)
					{
						ArtifactCapture.Download(j, chatUi, gptFile);
					}
					else
					{
						ArtifactCapture.Local(j, gptFile.LocalPath, false);
					}
					return;
				}
				string body = gptTurn.Body;
				int num = body.IndexOf(j.Begin, StringComparison.Ordinal);
				int num2 = body.IndexOf(j.End, StringComparison.Ordinal);
				if (num >= 0 && num2 > num && !Binary(j, intent))
				{
					string text = body.Substring(num + j.Begin.Length, num2 - num - j.Begin.Length).Trim('\r', '\n');
					if (!string.IsNullOrWhiteSpace(text))
					{
						ArtifactCapture.Finish(j, j.Name, null, text);
						return;
					}
				}
				if (!gptTurn.Assistant || !gptTurn.Completed || !Settled(j.Id, body, 3.0))
				{
					return;
				}
				throw new IOException("GPT 未生成所需文件。可在 GPT 中补充内容或指定已有附件。");
			}
			GptFile gptFile2 = Pick(turn.UserFiles, intent.Name);
			if (gptFile2 == null && turn.Files.Count > 0 && (!view.Streaming || turn != view.Turns.Last()))
			{
				gptFile2 = Pick(turn.Files, intent.Name);
			}
			if (gptFile2 == null && intent.Previous)
			{
				IEnumerable<GptTurn> enumerable = view.Turns.TakeWhile((GptTurn t) => t != turn).Reverse();
				foreach (GptTurn item in enumerable)
				{
					IEnumerable<GptFile> enumerable2 = item.Files.Concat(item.UserFiles);
					if (intent.Name != null)
					{
						enumerable2 = enumerable2.Where((GptFile f) => string.Equals(f.Name, intent.Name, StringComparison.OrdinalIgnoreCase));
					}
					gptFile2 = Pick(enumerable2, intent.Name);
					if (gptFile2 != null)
					{
						break;
					}
				}
			}
			if (gptFile2 != null)
			{
				if (gptFile2.LocalPath != null)
				{
					ArtifactCapture.Local(j, gptFile2.LocalPath, false);
				}
				else
				{
					ArtifactCapture.Download(j, chatUi, gptFile2);
				}
			}
			else
			{
				if (!turn.Assistant || (view.Streaming && turn == view.Turns.Last()) || !Settled(j.Id, turn.Body, turn.Completed ? 2 : 5))
				{
					return;
				}
				string body2 = turn.Body;
				if (string.IsNullOrWhiteSpace(body2))
				{
					return;
				}
				bool flag = Regex.IsMatch(body2, "请(?:先)?(?:告诉我|提供.{0,7}(?:内容|主题|素材))|你(?:想|希望).{0,9}(?:什么|哪种)|which file|what (?:would you like|should the file)", RegexOptions.IgnoreCase);
				if (intent.Create && !Binary(j, intent) && !flag)
				{
					string text2 = ExtractFence(body2);
					if (!Regex.IsMatch(body2, "无法直接|不能直接|不能代|没有(?:收到|看到)文件|请(?:先)?上传|请(?:先)?提供|你(?:想|希望)写什么|which file|cannot (?:send|access)|can't (?:send|access)|please (?:upload|provide)", RegexOptions.IgnoreCase) || text2 != null)
					{
						ArtifactCapture.Finish(j, j.Name, null, text2 ?? body2);
						return;
					}
				}
				if (!intent.Create || flag || turn != view.Turns.Last())
				{
					throw new IOException("这条 GPT 回复没有可确认的附件。可以在同一聊天中指定文件名或让 GPT 生成可下载的文件后发送。");
				}
				if (chatUi.HasDraftOrAttachment())
				{
					return;
				}
				string text3 = "双窗取件 " + j.Id + "。请按照上一条要求制作文件，双窗负责发送给 Claude。请在此回复里附上可下载的成品。";
				if (!Binary(j, intent))
				{
					text3 = text3 + "也可以用一个代码块输出正文。第一行写 DUO_BEGIN_ 紧接 " + j.Id + "，最后一行写 DUO_END_ 紧接同一个编号。";
				}
				chatUi.Send(text3, delegate
				{
					GptTurn gptTurn2 = GptTranscript.Read(handle).Turns.LastOrDefault();
					if (gptTurn2 == null || gptTurn2.Key != j.Origin)
					{
						throw new IOException("GPT 已切换聊天，已暂停取件。");
					}
					j.WriterFollowup = true;
					j.Detail = "正在等候 GPT 提供可转发的成品。";
					RelayStore.Save(j);
				});
			}
		}

		private static bool Binary(Handoff j, RelayIntent intent)
		{
			if (RelayIntent.IsText(j.Name))
			{
				return Regex.IsMatch(intent.Text, "(?<![a-z])(?:PDF|Word|Excel|PowerPoint|PPTX?|PNG|JPEG|ZIP)(?![a-z])|演示文稿|幻灯片|照片|图片", RegexOptions.IgnoreCase);
			}
			return true;
		}

		private static string ExtractFence(string body)
		{
			MatchCollection matchCollection = Regex.Matches(body, "(?:^|\\n)\\x60{3,}[^\\n]*\\n([\\s\\S]*?)\\n\\x60{3,}(?:\\n|$)");
			if (matchCollection.Count != 1)
			{
				return null;
			}
			return matchCollection[0].Groups[1].Value;
		}
	}
}
