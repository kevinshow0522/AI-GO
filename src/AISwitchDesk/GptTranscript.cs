using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace AISwitchDesk
{
	internal sealed class GptTranscript
	{
		internal readonly List<GptTurn> Turns = new List<GptTurn>();

		internal string Draft = "";

		internal string Page = "";

		internal bool HasEditor;

		internal bool Streaming;

		internal bool Codex;

		internal AutomationElement Composer;

		private static readonly AutomationProperty[] Props = new AutomationProperty[6]
		{
			AutomationElement.NameProperty,
			AutomationElement.ControlTypeProperty,
			AutomationElement.ClassNameProperty,
			AutomationElement.AutomationIdProperty,
			AutomationElement.LocalizedControlTypeProperty,
			ValuePattern.ValueProperty
		};

		private static readonly Regex UserLabel = new Regex("^(?:你[說说]|您[說说]|You said)\\s*[:：](?:$|\\s)", RegexOptions.IgnoreCase);

		private static readonly Regex AgentLabel = new Regex("^(?:(?:ChatGPT|GPT|Codex)\\s*(?:[說说]|said)|Assistant)\\s*[:：](?:$|\\s)", RegexOptions.IgnoreCase);

		private static int Heading(AutomationElement el, string name, string css, string type)
		{
			int num = (UserLabel.IsMatch(name) ? 1 : (AgentLabel.IsMatch(name) ? 2 : 0));
			if (num == 0)
			{
				return 0;
			}
			if (css.Contains("sr-only") || Regex.IsMatch(type, "heading|标题|標題", RegexOptions.IgnoreCase))
			{
				return num;
			}
			AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(el);
			if (parent != null && (parent.Current.ClassName.Contains("sr-only") || Regex.IsMatch(parent.Current.LocalizedControlType ?? "", "heading|标题|標題", RegexOptions.IgnoreCase)))
			{
				return num;
			}
			return 0;
		}

		internal static GptTranscript Read(IntPtr window)
		{
			try
			{
				return ReadSnapshot(window);
			}
			catch (ElementNotAvailableException)
			{
			}
			catch (COMException ex2)
			{
				if (ex2.ErrorCode != -2147220991)
				{
					throw;
				}
			}
			return ReadSnapshot(window);
		}

		private static GptTranscript ReadSnapshot(IntPtr window)
		{
			GptTranscript gptTranscript = new GptTranscript();
			AutomationElement automationElement = AutomationElement.FromHandle(window);
			CacheRequest cacheRequest = new CacheRequest();
			AutomationProperty[] props = Props;
			foreach (AutomationProperty property in props)
			{
				cacheRequest.Add(property);
			}
			AutomationElementCollection automationElementCollection;
			using (cacheRequest.Activate())
			{
				automationElementCollection = automationElement.FindAll(TreeScope.Descendants, Condition.TrueCondition);
			}
			GptTurn gptTurn = null;
			int num = 0;
			int num2 = 0;
			bool flag = false;
			TextPattern textPattern = null;
			foreach (AutomationElement item in automationElementCollection)
			{
				string text = (item.Cached.Name ?? "").Trim();
				string text2 = item.Cached.ClassName ?? "";
				ControlType controlType = item.Cached.ControlType;
				if (controlType == ControlType.Document)
				{
					string uriString = item.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
					Uri result;
					if (Uri.TryCreate(uriString, UriKind.Absolute, out result) && (result.Scheme == "app" || result.Host == "chatgpt.com"))
					{
						gptTranscript.Page = result.GetLeftPart(UriPartial.Path);
					}
					textPattern = ChatUi.Pattern<TextPattern>(item, TextPattern.Pattern) ?? textPattern;
				}
				if (controlType == ControlType.Edit && (text2.IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0 || item.Cached.AutomationId == "prompt-textarea"))
				{
					gptTranscript.HasEditor = true;
					gptTranscript.Composer = item;
					gptTranscript.Draft = ((item.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string) ?? "").Trim();
					num = 0;
					continue;
				}
				if (controlType == ControlType.Button)
				{
					if (gptTurn != null && Regex.IsMatch(text, "(?:^|[. :])handoff[ _]file$", RegexOptions.IgnoreCase))
					{
						gptTurn.ToolHandoff = true;
					}
					if (Regex.IsMatch(text, "^(?:切换模式|切換模式|Switch mode).*(?:工作|Codex)$", RegexOptions.IgnoreCase))
					{
						gptTranscript.Codex = true;
					}
					if (Regex.IsMatch(text, "^(停止(?:生成|回复|响应|回答|流式传输)?|停止產生|Stop(?: generating| streaming| response)?)$", RegexOptions.IgnoreCase))
					{
						gptTranscript.Streaming = true;
					}
					if (num == 1 && Regex.IsMatch(text, "^(?:复制消息|複製訊息|编辑消息|編輯訊息|Copy message|Edit message)$", RegexOptions.IgnoreCase))
					{
						num = 0;
						continue;
					}
					if (gptTurn != null && num == 2 && Regex.IsMatch(text, "^(?:复制回复|複製回覆|复制消息|Copy response|Copy message|复制|複製|Copy)$", RegexOptions.IgnoreCase))
					{
						gptTurn.Completed = true;
						num = 0;
						continue;
					}
				}
				int num3 = Heading(item, text, text2, item.Cached.LocalizedControlType ?? "");
				if (num3 != 0)
				{
					if (num3 == 1 && (!flag || num != 1))
					{
						GptTurn gptTurn2 = new GptTurn();
						gptTurn2.ElementId = string.Join(".", from x in item.GetRuntimeId()
							select x.ToString());
						gptTurn = gptTurn2;
						gptTranscript.Turns.Add(gptTurn);
						num2 = 0;
					}
					if (num3 == 2 && gptTurn != null)
					{
						gptTurn.Assistant = true;
					}
					num = num3;
					flag = true;
				}
				else
				{
					if (gptTurn == null || num == 0)
					{
						continue;
					}
					if (controlType == ControlType.Hyperlink || controlType == ControlType.Button)
					{
						string value = item.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
						GptFile file = GptFile.From(text, value, text2, controlType, item);
						if (file != null)
						{
							List<GptFile> list = ((num == 1) ? gptTurn.UserFiles : gptTurn.Files);
							if (!list.Any((GptFile x) => x.Name == file.Name && x.LocalPath == file.LocalPath))
							{
								list.Add(file);
							}
						}
					}
					if (controlType != ControlType.Text || text.Length == 0 || UserLabel.IsMatch(text) || AgentLabel.IsMatch(text) || Regex.IsMatch(text, "^(?:\\d{1,2}:\\d{2}(?:\\s*[AP]M)?|复制代码|复制回复|Copy code|Copy response|ChatGPT 也可能会犯错。.*)$", RegexOptions.IgnoreCase) || text2.Contains("sr-only"))
					{
						continue;
					}
					num2 += text.Length;
					if (num2 > 1000000)
					{
						continue;
					}
					((num == 1) ? gptTurn.User : gptTurn.Answer).Add(text);
					if (num == 2)
					{
						if (gptTurn.FirstAnswer == null)
						{
							gptTurn.FirstAnswer = item;
						}
						gptTurn.LastAnswer = item;
					}
					flag = false;
				}
			}
			string text3 = "gpt";
			foreach (GptTurn turn in gptTranscript.Turns)
			{
				turn.Prompt = string.Join("\n", turn.User).Trim();
				if (turn.Answer.Count > 1 && textPattern != null && (RelayIntent.Parse(turn.Prompt) != null || turn.Prompt.StartsWith("双窗取件 ")))
				{
					try
					{
						TextPatternRange textPatternRange = textPattern.RangeFromChild(turn.FirstAnswer);
						TextPatternRange targetRange = textPattern.RangeFromChild(turn.LastAnswer);
						textPatternRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, targetRange, TextPatternRangeEndpoint.End);
						string text4 = textPatternRange.GetText(1000001).Replace("\r\n", "\n").Replace('\r', '\n')
							.Trim(' ', '\n', '￼');
						if (text4.Length > 0 && text4.Length <= 1000000 && text4.Contains(turn.Answer[0]) && text4.Contains(turn.Answer[turn.Answer.Count - 1]))
						{
							turn.Verbatim = text4;
						}
					}
					catch (InvalidOperationException)
					{
					}
					catch (ArgumentException)
					{
					}
					catch (ElementNotAvailableException)
					{
					}
				}
				text3 = (turn.Key = RelayIntent.Fingerprint(text3 + "\n" + turn.Prompt));
			}
			return gptTranscript;
		}
	}
}
