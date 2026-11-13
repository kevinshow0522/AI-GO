using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal static class RoundTrips
	{
		internal static bool Requested(string text)
		{
			text = text ?? "";
			if (Regex.IsMatch(text, "(?:不必|不要|不用|无需|無需|别|不用再).{0,6}(?:回传|回傳|发回|傳回|返回)|(?:do not|don't|no need to)\\s+(?:send (?:it )?back|return)", RegexOptions.IgnoreCase))
			{
				return false;
			}
			return Regex.IsMatch(text, "改完|修改|修订|修訂|改写|改寫|改一下|幫我改|帮我改|润色|潤色|审一遍|审稿|審稿|批注|校对|校對|完善|优化|優化|翻译|翻譯|编辑|編輯|回传|回傳|发回|發回|传回|傳回|交回|改好|修复|修復|加工|\\b(?:edit|revise|rewrite|proofread|annotate|polish|translate|improve|fix|review|return|send (?:it |the .{1,30} )?back)\\b", RegexOptions.IgnoreCase);
		}

		internal static Handoff Accept(string id, string name, Stream input)
		{
			string path = RelayStore.Folder(id);
			name = RelayStore.SafeName(name);
			if (!RelayIntent.IsFileName(name))
			{
				throw new ArgumentException("请提交带有支持扩展名的成品文件。");
			}
			using (Mutex mutex = new Mutex(false, "Local\\AISwitchDesk.Relay." + id))
			{
				try
				{
					if (!mutex.WaitOne(5000))
					{
						throw new IOException("回传正在写入，请稍后重试。");
					}
				}
				catch (AbandonedMutexException)
				{
				}
				try
				{
					Handoff handoff = RelayStore.Load(id);
					if (!handoff.RoundTrip || handoff.To != "Claude" || handoff.Stage == Stages.Cancel || (handoff.Stage != Stages.Confirm && handoff.Stage != Stages.AwaitResult && !handoff.Returning && handoff.Stage != Stages.Done))
					{
						throw new InvalidOperationException("这项交接当前不接收回传文件。");
					}
					string text = Path.Combine(path, "result");
					Directory.CreateDirectory(text);
					string text2 = Path.Combine(text, Guid.NewGuid().ToString("N") + ".part");
					try
					{
						string text3;
						using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
						{
							input.CopyTo(fileStream);
							fileStream.Position = 0L;
							using (SHA256 sHA = SHA256.Create())
							{
								text3 = BitConverter.ToString(sHA.ComputeHash(fileStream)).Replace("-", "");
							}
						}
						if (handoff.ReturnHash != null)
						{
							if (handoff.ReturnHash != text3 || handoff.ReturnName != name)
							{
								throw new InvalidOperationException("这项交接已收到另一份成品。");
							}
							return handoff;
						}
						if (handoff.Stage != Stages.Confirm && handoff.Stage != Stages.AwaitResult)
						{
							throw new InvalidOperationException("这项交接已经结束。");
						}
						string text4 = Path.Combine(text, name);
						if (File.Exists(text4))
						{
							File.Replace(text2, text4, null);
						}
						else
						{
							File.Move(text2, text4);
						}
						handoff.ReturnName = name;
						handoff.ReturnHash = text3;
						if (handoff.Stage == Stages.AwaitResult)
						{
							handoff.Stage = Stages.ReturnReady;
							handoff.Held = false;
						}
						handoff.Detail = "已保存 Claude 的成品，准备回到原 GPT 对话。";
						RelayStore.Save(handoff);
						return handoff;
					}
					finally
					{
						if (File.Exists(text2))
						{
							File.Delete(text2);
						}
					}
				}
				finally
				{
					mutex.ReleaseMutex();
				}
			}
		}

		internal static void Observe(Handoff j, ChatUi ui)
		{
			if (!ConversationRoute.Matches(ui, j.ClaudeRoute, null))
			{
				throw new ConversationPending("正在定位本次交接的 Claude 对话。");
			}
			if (j.DownloadStarted != 0)
			{
				ArtifactCapture.Download(j, ui, null);
			}
			else
			{
				if (!ui.TranscriptContains(j.Tag) || ChatUi.Of(ui.Root, ControlType.Button).Any((AutomationElement b) => !b.Current.IsOffscreen && Regex.IsMatch(ChatUi.Name(b), "^(?:停止(?:生成|回复|回覆|响应)?|Stop(?: generating| response| streaming)?)$", RegexOptions.IgnoreCase)))
				{
					return;
				}
				IEnumerable<AutomationElement> enumerable = ui.Root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>();
				foreach (AutomationElement item in enumerable)
				{
					if (item.Current.ControlType != ControlType.Hyperlink && item.Current.ControlType != ControlType.Button && item.Current.ControlType != ControlType.Text)
					{
						continue;
					}
					string text = ChatUi.Name(item);
					if (text.Length > j.SuggestedResult.Length + 45 || text.IndexOf(j.SuggestedResult, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}
					AutomationElement automationElement = null;
					if (item.Current.ControlType == ControlType.Hyperlink)
					{
						ValuePattern valuePattern = ChatUi.Pattern<ValuePattern>(item, ValuePattern.Pattern);
						Uri result;
						if (valuePattern != null && Uri.TryCreate(valuePattern.Current.Value, UriKind.Absolute, out result) && ((result.Scheme == "https" && result.Host == "claude.ai" && (result.AbsolutePath.StartsWith("/api/") || result.AbsolutePath.StartsWith("/usercontent/"))) || (result.Scheme == "sandbox" && result.AbsolutePath.StartsWith("/mnt/user-data/outputs/"))))
						{
							automationElement = item;
						}
					}
					else if (item.Current.ControlType == ControlType.Button && Regex.IsMatch(text, "download|下载|下載", RegexOptions.IgnoreCase))
					{
						automationElement = item;
					}
					else if (text == j.SuggestedResult)
					{
						AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(item);
						int num = 0;
						while (num < 3 && parent != null && parent.Current.ControlType != ControlType.Document)
						{
							AutomationElement[] array = (from b in ChatUi.Of(parent, ControlType.Button)
								where Regex.IsMatch(ChatUi.Name(b), "^(?:Download(?: file)?|下载(?:文件)?|下載(?:檔案)?)$", RegexOptions.IgnoreCase)
								select b).ToArray();
							if (array.Length == 1)
							{
								automationElement = array[0];
								break;
							}
							if (array.Length > 1)
							{
								break;
							}
							num++;
							parent = TreeWalker.ControlViewWalker.GetParent(parent);
						}
					}
					if (automationElement != null)
					{
						ui.CheckRoute();
						ArtifactCapture.Download(j, ui, new GptFile
						{
							Name = j.SuggestedResult,
							Element = automationElement
						});
						return;
					}
				}
				if (!RelayIntent.IsText(j.Name))
				{
					return;
				}
				string text2 = ui.MarkedBody("DUO_RETURN_BEGIN_" + j.Id, "DUO_RETURN_END_" + j.Id);
				if (text2 != null)
				{
					ui.CheckRoute();
					using (MemoryStream input = new MemoryStream(RelayStore.Utf8.GetBytes(text2)))
					{
						Accept(j.Id, j.SuggestedResult, input);
					}
				}
			}
		}
	}
}
