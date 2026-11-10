using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace AISwitchDesk
{
	internal sealed class ChatUi
	{
		internal readonly IntPtr Window;

		private readonly Func<bool> canAct;

		private static readonly TreeScope Desc = TreeScope.Descendants;

		internal string ExpectedRoute;

		internal string ExpectedProof;

		internal Action CheckDraftTarget;

		internal int[] DraftEditorId;

		internal double[] DraftEditorBox;

		internal AutomationElement Root
		{
			get
			{
				return AutomationElement.FromHandle(Window);
			}
		}

		internal bool PickingFile
		{
			get
			{
				if (!(FileDialog() != null))
				{
					return ChooseLocal() != null;
				}
				return true;
			}
		}

		internal ChatUi(IntPtr window, Func<bool> allowed)
		{
			Window = window;
			canAct = allowed;
		}

		internal static IEnumerable<AutomationElement> Of(AutomationElement root, ControlType kind)
		{
			if (root == null)
			{
				return new AutomationElement[0];
			}
			return root.FindAll(Desc, new PropertyCondition(AutomationElement.ControlTypeProperty, kind)).Cast<AutomationElement>().ToArray();
		}

		internal static T Pattern<T>(AutomationElement e, AutomationPattern p) where T : class
		{
			object patternObject;
			if (!(e != null) || !e.TryGetCurrentPattern(p, out patternObject))
			{
				return null;
			}
			return patternObject as T;
		}

		internal static string Name(AutomationElement e)
		{
			return (e.Current.Name ?? "").Trim();
		}

		internal static string Value(AutomationElement e)
		{
			ValuePattern valuePattern = Pattern<ValuePattern>(e, ValuePattern.Pattern);
			if (valuePattern != null)
			{
				return valuePattern.Current.Value ?? "";
			}
			TextPattern textPattern = Pattern<TextPattern>(e, TextPattern.Pattern);
			if (textPattern != null)
			{
				return textPattern.DocumentRange.GetText(-1);
			}
			return "";
		}

		internal static Uri Page(AutomationElement doc)
		{
			ValuePattern valuePattern = Pattern<ValuePattern>(doc, ValuePattern.Pattern);
			Uri result;
			if (valuePattern == null || !Uri.TryCreate(valuePattern.Current.Value, UriKind.Absolute, out result))
			{
				return null;
			}
			return result;
		}

		internal string PageKey()
		{
			foreach (AutomationElement item in Of(Root, ControlType.Document).Reverse())
			{
				Uri uri = Page(item);
				if (uri != null && (uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) || uri.Scheme == "app"))
				{
					return uri.GetLeftPart(UriPartial.Path);
				}
			}
			return "";
		}

		internal void CheckRoute()
		{
			if (ExpectedRoute != null && !ConversationRoute.Matches(this, ExpectedRoute, ExpectedProof))
			{
				throw new ConversationPending("对话已切换，后台会重新定位原对话。");
			}
		}

		private void Guard(bool pickingFile = false)
		{
			if (!Native.IsWindow(Window) || !canAct())
			{
				throw new InvalidOperationException("请返回双窗的目标应用，然后继续交接。");
			}
			if (!pickingFile) CheckRoute();
			if (!pickingFile && CheckDraftTarget != null)
			{
				CheckDraftTarget();
			}
		}

		internal AutomationElement Editor()
		{
			List<AutomationElement> list = (from e in Of(Root, ControlType.Edit)
				where !e.Current.IsOffscreen && e.Current.IsEnabled && (e.Current.ClassName.IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0 || e.Current.AutomationId == "prompt-textarea")
				select e).ToList();
			if (DraftEditorId != null)
			{
				AutomationElement pinned = list.FirstOrDefault(e => DraftEditorId.SequenceEqual(e.GetRuntimeId()));
				if (pinned != null) return pinned;
				if (DraftEditorBox != null)
				{
					int rebound = ChooseEditorByBounds(list.Select(e => e.Current.BoundingRectangle.X).ToArray(),
						list.Select(e => e.Current.BoundingRectangle.Y).ToArray(),list.Select(e => e.Current.BoundingRectangle.Width).ToArray(),
						list.Select(e => e.Current.BoundingRectangle.Height).ToArray(), DraftEditorBox);
					if (rebound >= 0) { DraftEditorId = list[rebound].GetRuntimeId(); return list[rebound]; }
				}
				throw new InvalidOperationException("目标聊天的输入框已切换，已停止添加文件。");
			}
			int selected = ChooseEditorIndex(list.Select(AttachmentDepth).ToArray(),
				list.Select(e => e.Current.HasKeyboardFocus || e.Current.ClassName.Contains("ProseMirror-focused")).ToArray(),
				list.Select(e => e.Current.AutomationId == "prompt-textarea" || IsChatEditorName(Name(e))).ToArray());
			if (selected < 0)
			{
				DropTrace.Write("ui", "editor-missing matched=" + list.Count + " edits=" + string.Join(";", Of(Root, ControlType.Edit).Select(e => e.Current.AutomationId + "/" + e.Current.ClassName + "/off=" + e.Current.IsOffscreen + "/enabled=" + e.Current.IsEnabled)));
				foreach (AutomationElement candidate in list)
				{
					DropTrace.Write("ui", "candidate name=" + Name(candidate) + " help=" + candidate.Current.HelpText + " box=" + candidate.Current.BoundingRectangle);
					AutomationElement ancestor = TreeWalker.ControlViewWalker.GetParent(candidate);
					for (int depth = 1; ancestor != null && depth <= 7 && ancestor.Current.ControlType != ControlType.Document; depth++)
					{
						DropTrace.Write("ui", "ancestor " + depth + " " + ancestor.Current.ControlType.ProgrammaticName + "/" + ancestor.Current.ClassName + " buttons=" + string.Join("|", Of(ancestor,ControlType.Button).Where(b => !b.Current.IsOffscreen).Take(12).Select(Name)));
						ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor);
					}
				}
				throw new InvalidOperationException(list.Count == 0 ? "请登录并打开一个可输入的聊天，再拖入文件。" : "检测到多个编辑区，暂未确定聊天输入框。文件尚未加入。");
			}
			return list[selected];
		}

		private int AttachmentDepth(AutomationElement edit)
		{
			AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(edit);
			for (int depth = 1; parent != null && depth <= 10 && parent.Current.ControlType != ControlType.Document && parent.Current.ControlType != ControlType.Window; depth++)
			{
				if (Of(parent, ControlType.Button).Any(b => !b.Current.IsOffscreen && IsAdd(b)))
				{
					int editors = Of(parent, ControlType.Edit).Count(e => !e.Current.IsOffscreen &&
						(e.Current.ClassName.Contains("ProseMirror") || e.Current.AutomationId == "prompt-textarea"));
					return editors == 1 ? depth : int.MaxValue;
				}
				parent = TreeWalker.ControlViewWalker.GetParent(parent);
			}
			return int.MaxValue;
		}

		internal static int ChooseEditorIndex(int[] attachmentDepth, bool[] focused, bool[] promptId)
		{
			if (attachmentDepth.Length == 0) return -1;
			if (attachmentDepth.Length == 1) return 0;
			int[] ids = Enumerable.Range(0, attachmentDepth.Length).Where(i => promptId[i]).ToArray();
			if (ids.Length == 1) return ids[0];
			int closest = attachmentDepth.Min();
			int[] choices = Enumerable.Range(0, attachmentDepth.Length).Where(i => attachmentDepth[i] == closest).ToArray();
			if (closest != int.MaxValue && choices.Length == 1) return choices[0];
			int[] active = choices.Where(i => focused[i]).ToArray();
			return active.Length == 1 ? active[0] : -1;
		}

		internal static bool IsChatEditorName(string name)
		{
			return IsNewChatEditorName(name) || Match(Clean(name), "^(?:和\\s*(?:Codex|ChatGPT)\\s*一起工作|与\\s*(?:Codex|ChatGPT)\\s*一起工作|Work with (?:Codex|ChatGPT)|使用 ChatGPT Work|Message (?:Claude|ChatGPT)|Ask anything|Ask (?:Codex|ChatGPT)|给 Claude 发消息)$");
		}

		private static bool IsNewChatEditorName(string name)
		{
			return Match(Clean(name), "^(?:随心输入|询问 ChatGPT|Ask anything|向\\s*(?:Codex|ChatGPT)\\s*下达任意指令|让\\s*Codex\\s*在云端完成任何任务|可向\\s*(?:Codex|ChatGPT)\\s*询问任何问题。输入\\s*@\\s*使用插件或提及文件|Ask Codex to do anything|Ask (?:Codex|ChatGPT) anything[.!]? Use @ (?:for|to use) plugins or (?:mention )?files[.!]?)$");
		}

		internal static int ChooseEditorByBounds(double[] x, double[] y, double[] width, double[] height, double[] expected)
		{
			if (expected == null || expected.Length != 4 || expected[2] < 2 || expected[3] < 2) return -1;
			int[] matches = Enumerable.Range(0,x.Length).Where(i => width[i] >= 2 && height[i] >= 2 &&
				Math.Abs(x[i]-expected[0]) <= 4 && Math.Abs(y[i]-expected[1]) <= 4 && Math.Abs(width[i]-expected[2]) <= 4).ToArray();
			return matches.Length == 1 ? matches[0] : -1;
		}

		internal void WaitForEditor()
		{
			AutomationElement automationElement = Until(delegate
			{
				try
				{
					return Editor();
				}
				catch (InvalidOperationException)
				{
					return (AutomationElement)null;
				}
			}, 8000);
			if (automationElement == null)
			{
				throw new InvalidOperationException("请先登录并打开聊天输入框，再重试。");
			}
		}

		private static string Clean(string s)
		{
			return (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim(' ', '\n', '\t', '\u200b', '￼');
		}

		private static string DraftText(AutomationElement editor)
		{
			return NormalizeDraftText(Value(editor),Name(editor),editor.Current.HelpText);
		}

		internal static string NormalizeDraftText(string value, string name, string help)
		{
			string text = Clean(value);
			if ((text == Clean(name) || text == Clean(help)) && IsChatEditorName(text)) return "";
			return text;
		}

		private static bool SameText(string a, string b)
		{
			return Regex.Replace(Clean(a), "\\s+", " ") == Regex.Replace(Clean(b), "\\s+", " ");
		}

		internal static T Until<T>(Func<T> check, int milliseconds) where T : class
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			do
			{
				try
				{
					T val = check();
					if (val != null)
					{
						return val;
					}
				}
				catch (ElementNotAvailableException)
				{
				}
				Thread.Sleep(160);
			}
			while (stopwatch.ElapsedMilliseconds < milliseconds);
			return null;
		}

		private static bool Match(string n, string regex)
		{
			return Regex.IsMatch(n, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		private AutomationElement ComposerControl(AutomationElement edit, Func<AutomationElement, bool> predicate)
		{
			AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(edit);
			int num = 0;
			while (parent != null && num < 10 && parent.Current.ControlType != ControlType.Document && parent.Current.ControlType != ControlType.Window)
			{
				AutomationElement automationElement = Of(parent, ControlType.Button).FirstOrDefault((AutomationElement b) => !b.Current.IsOffscreen && b.Current.IsEnabled && predicate(b));
				if (automationElement != null)
				{
					return automationElement;
				}
				num++;
				parent = TreeWalker.ControlViewWalker.GetParent(parent);
			}
			return null;
		}

		private static bool IsAdd(AutomationElement b)
		{
			return Match(Name(b), "^(添加文件|添加附件|上传文件|新增檔案|新增文件|加入檔案|附加文件|Add files|Add attachments|Attach files|Add photos|Open attachment|Open add menu)");
		}

		private static bool IsSend(AutomationElement b)
		{
			if (!Match(b.Current.AutomationId, "^(send-button|composer-submit-button)$"))
			{
				return Match(Name(b), "^(发送|发送消息|发送提示|发送提示词|发送内容|提交|傳送|傳送訊息|送出|送出訊息|Send|Send message|Send prompt|Submit)(\\s*[\\(（].*)?$");
			}
			return true;
		}

		private void Act(AutomationElement e)
		{
			Guard();
			ActCore(e);
		}

		private void ActFresh(Func<AutomationElement> resolve)
		{
			Guard();
			ActCore(resolve());
		}

		private void ActCore(AutomationElement e)
		{
			if (e == null || !e.Current.IsEnabled || e.Current.IsOffscreen)
			{
				throw new InvalidOperationException("未找到可用的界面按钮。");
			}
			InvokePattern invokePattern = Pattern<InvokePattern>(e, InvokePattern.Pattern);
			SelectionItemPattern selectionItemPattern = Pattern<SelectionItemPattern>(e, SelectionItemPattern.Pattern);
			DropTrace.Write("ui", "act " + e.Current.ControlType.ProgrammaticName + "/" + Name(e) +
				" invoke=" + (invokePattern != null) + " selection=" + (selectionItemPattern != null) +
				" selected=" + (selectionItemPattern != null && selectionItemPattern.Current.IsSelected));
			if (invokePattern != null)
			{
				invokePattern.Invoke();
				return;
			}
			// SelectionItem.Select is a no-op when this menu option is already
			// selected. Execute its default action instead of merely highlighting it.
			if (selectionItemPattern != null && IsUploadEntry(e.Current.ControlType.ProgrammaticName, Name(e)) &&
				UiaAction.TryInvokeDefault(Window, e.GetRuntimeId())) return;
			ExpandCollapsePattern expandCollapsePattern = Pattern<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern);
			if (expandCollapsePattern != null)
			{
				DropTrace.Write("ui", "expand-state=" + expandCollapsePattern.Current.ExpandCollapseState);
				if (expandCollapsePattern.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
				{
					expandCollapsePattern.Expand();
				}
				return;
			}
			if (selectionItemPattern != null)
			{
				selectionItemPattern.Select();
				return;
			}
			throw new InvalidOperationException("此客户端暂未提供可操作的按钮。");
		}

		internal AutomationElement FileDialog(bool saving = false)
		{
			uint wanted;
			Native.GetWindowThreadProcessId(Window, out wanted);
			string family = Native.PackageOf(wanted);
			AutomationElement found = null;
			Native.EnumWindows(delegate(IntPtr h, IntPtr unused)
			{
				if (!Native.IsWindowVisible(h))
				{
					return true;
				}
				StringBuilder stringBuilder = new StringBuilder(64);
				Native.GetClassName(h, stringBuilder, stringBuilder.Capacity);
				if (stringBuilder.ToString() != "#32770")
				{
					return true;
				}
				StringBuilder stringBuilder2 = new StringBuilder(120);
				Native.GetWindowText(h, stringBuilder2, stringBuilder2.Capacity);
				if (saving && !Match(stringBuilder2.ToString(), "另存[为為]|保存|儲存|save"))
				{
					return true;
				}
				if (!saving && Match(stringBuilder2.ToString(), "另存[为為]|保存|儲存|save"))
				{
					return true;
				}
				uint pid;
				Native.GetWindowThreadProcessId(h, out pid);
				IntPtr window = Native.GetWindow(h, 4u);
				if (window != Window && window != Native.GetAncestor(Window, 2u) && !Native.IsChild(Window, window))
				{
					return true;
				}
				if (pid != wanted)
				{
					if (family.Length > 0)
					{
						if (Native.PackageOf(pid) != family)
						{
							return true;
						}
					}
					else
					{
						string text = Native.ImageOf(wanted);
						if (text.Length == 0 || Native.PackageOf(pid).Length > 0 || !string.Equals(text, Native.ImageOf(pid), StringComparison.OrdinalIgnoreCase))
						{
							return true;
						}
					}
				}
				AutomationElement automationElement = AutomationElement.FromHandle(h);
				if (automationElement.FindFirst(Desc, new PropertyCondition(AutomationElement.AutomationIdProperty, "1148")) == null)
				{
					return true;
				}
				found = automationElement;
				return false;
			}, IntPtr.Zero);
			return found;
		}

		private AutomationElement UploadItem()
		{
			var roles = new OrCondition(new Condition[] {
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink) });
			var cache = new CacheRequest();
			cache.Add(AutomationElement.NameProperty);
			cache.Add(AutomationElement.ControlTypeProperty);
			cache.Add(AutomationElement.IsOffscreenProperty);
			cache.Add(AutomationElement.IsEnabledProperty);
			AutomationElement[] candidates;
			using (cache.Activate()) candidates = Root.FindAll(Desc, roles).Cast<AutomationElement>().ToArray();
			return candidates.FirstOrDefault(e => !e.Cached.IsOffscreen && e.Cached.IsEnabled && IsUploadEntry(e.Cached.ControlType.ProgrammaticName,(e.Cached.Name ?? "").Trim()));
		}

		internal static bool IsUploadEntry(string role, string name)
		{
			return Match(role ?? "", "^ControlType\\.(Button|MenuItem|ListItem|Hyperlink)$") && Match(name ?? "", "^(文件和文件夹|檔案和資料夾|Files (?:and|&) folders|从(计算机|电脑|设备)上传|從(電腦|裝置)上傳|上传或添加文件|上傳或新增檔案|上传文件|上传照片和文件|上傳檔案|添加文件或照片|添加照片和文件|新增相片和檔案|Upload (or add files|a |from |files|file|photos)|Add (files or photos|photos (and|&) files)|Choose files|Add from computer|从电脑添加|本地文件)");
		}

		private AutomationElement ChooseLocal()
		{
			return Of(Root, ControlType.Button).FirstOrDefault((AutomationElement e) => !e.Current.IsOffscreen && e.Current.IsEnabled && Match(Name(e), "^(选择文件|选取文件|選擇檔案|選取檔案|Choose files?|Select files?|Browse files?)$"));
		}

		private static bool AttachmentAction(string n)
		{
			return Match(n, "^(移除|删除|移除附件|移除文件|刪除|Remove|Delete attachment|Remove attachment|Remove file)");
		}

		private AutomationElement ComposerArea(AutomationElement edit)
		{
			AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(edit);
			int num = 0;
			while (parent != null && num < 10 && parent.Current.ControlType != ControlType.Document)
			{
				if (Of(parent, ControlType.Button).Any(IsAdd))
				{
					return parent;
				}
				num++;
				parent = TreeWalker.ControlViewWalker.GetParent(parent);
			}
			return null;
		}

		internal bool HasDraftOrAttachment()
		{
			AutomationElement item = Editor();
			return DraftText(item).Length > 0 || HasAttachments(item);
		}

		private static bool IsAttachmentNode(AutomationElement node, AutomationElement area, AutomationElement editor)
		{
			// Chromium can expose the model version as a separate text node in the
			// selector button. The same string is a valid filename on a real chip.
			while (node != null && !Automation.Compare(node, area))
			{
				if (Automation.Compare(node, editor) || (node.Current.ControlType == ControlType.Button &&
					Match(Name(node), @"^(?:模型|Model)\s*[:：]"))) return false;
				node = TreeWalker.ControlViewWalker.GetParent(node);
			}
			return node != null;
		}

		internal bool HasAttachments(AutomationElement editor, bool includePending = true)
		{
			AutomationElement automationElement = ComposerArea(editor);
			if (automationElement == null)
			{
				return false;
			}
			if (DraftDrop.Protects(this, includePending))
			{
				return true;
			}
			if (Of(automationElement, ControlType.Text).Any(e => !e.Current.IsOffscreen &&
				Regex.IsMatch(Name(e), "^[^\\r\\n/\\\\]{1,220}\\.[a-zA-Z0-9]{1,12}$") && IsAttachmentNode(e, automationElement, editor)))
			{
				return true;
			}
			AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(automationElement);
			if (parent != null && parent.Current.ControlType != ControlType.Document)
			{
				automationElement = parent;
			}
			return Of(automationElement, ControlType.Button).Any((AutomationElement e) => !e.Current.IsOffscreen && AttachmentAction(Name(e)));
		}

		internal bool HasFile(string file)
		{
			string stem = Path.GetFileNameWithoutExtension(file);
			AutomationElement edit;
			try { edit = Editor(); }
			catch (InvalidOperationException) { return false; }
			var area = ComposerArea(edit);
			if (area == null) return false;
			var parent = TreeWalker.ControlViewWalker.GetParent(area);
			if (parent != null && parent.Current.ControlType != ControlType.Document) area = parent;
			return area.FindAll(Desc,Condition.TrueCondition).Cast<AutomationElement>().Any(e => !e.Current.IsOffscreen &&
				(string.Equals(Name(e),file,StringComparison.OrdinalIgnoreCase) ||
				(stem.Length >= 2 && string.Equals(Name(e),stem,StringComparison.OrdinalIgnoreCase)) ||
				(Name(e).IndexOf(file,StringComparison.OrdinalIgnoreCase) >= 0 && (AttachmentAction(Name(e)) || Name(e).StartsWith(file,StringComparison.OrdinalIgnoreCase)))) &&
				IsAttachmentNode(e, area, edit));
		}

		internal bool HasDraftFile(string file)
		{
			AutomationElement edit;
			try
			{
				edit = Editor();
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			AutomationElement area = ComposerArea(edit);
			if (area == null)
			{
				return false;
			}
			var cache = new CacheRequest();
			cache.Add(AutomationElement.NameProperty);
			cache.Add(AutomationElement.IsOffscreenProperty);
			using (cache.Activate())
			{
				foreach (AutomationElement node in area.FindAll(Desc,Condition.TrueCondition))
				{
					if (node.Cached.IsOffscreen) continue;
					string label = (node.Cached.Name ?? "").Trim();
					if (RemovalNamesFile(label,file)) return true;
					if ((label.Equals(file,StringComparison.OrdinalIgnoreCase) ||
						(label.StartsWith(file,StringComparison.OrdinalIgnoreCase) && label.Length > file.Length && char.IsWhiteSpace(label[file.Length]))) &&
						IsAttachmentNode(node, area, edit)) return true;
				}
				// Some clients place the removable chips immediately above the form.
				// A filename in the surrounding message history isn't a draft chip.
				var parent = TreeWalker.ControlViewWalker.GetParent(area);
				if (parent == null || parent.Current.ControlType == ControlType.Document) return false;
				return Of(parent,ControlType.Button).Any(b => !b.Cached.IsOffscreen && RemovalNamesFile(b.Cached.Name ?? "",file));
			}
		}

		private static bool RemovalNamesFile(string label,string file)
		{
			if (!AttachmentAction(label)) return false;
			string trimmed = label.TrimEnd(' ', '」', '”', '"', '’', '\'');
			if (!trimmed.EndsWith(file,StringComparison.OrdinalIgnoreCase)) return false;
			string prefix = trimmed.Substring(0,trimmed.Length-file.Length);
			return prefix.Length > 0 && (char.IsWhiteSpace(prefix[prefix.Length-1]) ||
				"「“‘\"'(".Contains(prefix[prefix.Length-1]) ||
				Match(prefix,"^(移除|删除|刪除)(附件|文件|檔案)?$"));
		}

		private void EmptyOrOwned(AutomationElement editor, string ours)
		{
			string text = DraftText(editor);
			if (text.Length != 0 && !SameText(text, ours))
			{
				throw new InvalidOperationException("目标输入框已有草稿。请先处理草稿，再点继续。");
			}
		}

		private bool HasConversationHistory(string app)
		{
			if (app == "ChatGPT" && GptTranscript.Read(Window).Turns.Count > 0) return true;
			return Of(Root, ControlType.Button).Any(e => !e.Current.IsOffscreen &&
				Match(Name(e), "^(复制回复|複製回覆|Copy response|Edit message|编辑消息|编辑提示)$"));
		}

		// Keep navigation separate from relay jobs: a manual drop only creates a draft.
		internal void OpenNewDraft(string app)
		{
			Guard();
			AutomationElement before = Editor();
			string previousPage = PageKey();
			int[] previousEditor = before.GetRuntimeId();
			bool previousHistory = HasConversationHistory(app);
			DropTrace.Write(app,"new-chat-before page=" + previousPage + " history=" + previousHistory + " editor=" + Name(before));
			AutomationElement control = Of(Root, ControlType.Button).Concat(Of(Root, ControlType.Hyperlink))
				.FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.IsEnabled &&
					(Match(Name(e), "^(新聊天|新建聊天|新对话|新建对话|新增對話|新任务|新建任务|New chat|Start new chat|New conversation|New thread|New task)(?:\\s*[\\(（].*|\\s+(?:Ctrl|Control|⌘).*)?$") ||
					 Match(e.Current.AutomationId, "^(new-chat-button|new-thread-button|new-conversation-button)$")));
			if (control == null && app == "Claude")
			{
				control = Of(Root, ControlType.Hyperlink).FirstOrDefault(e =>
				{
					ValuePattern value = Pattern<ValuePattern>(e, ValuePattern.Pattern);
					Uri uri;
					return !e.Current.IsOffscreen && e.Current.IsEnabled && value != null &&
						Uri.TryCreate(value.Current.Value, UriKind.Absolute, out uri) &&
						uri.Host == "claude.ai" && uri.AbsolutePath.TrimEnd('/') == "/new";
				});
			}
			if (control == null) throw new InvalidOperationException("未找到「新建聊天」入口。请在目标 AI 中手动新建聊天，再拖入文件并选择「放入当前对话」。");
			Act(control);
			Thread.Sleep(250);
			int stable = 0;
			string lastNewState = null;
			string confirmed = Until(delegate
			{
				Guard();
				AutomationElement editor;
				try { editor = Editor(); } catch (InvalidOperationException) { stable = 0; return null; }
				string page = PageKey();
				bool history = HasConversationHistory(app);
				// The previous conversation can contain an unsent draft. Only the
				// destination must be empty. A recent drop on the previous draft
				// can share the same generic app URL with this new composer.
				bool draft = DraftText(editor).Length > 0 || HasAttachments(editor, false);
				bool newSurface = IsNewChatEditorName(Name(editor)) || IsNewChatEditorName(editor.Current.HelpText);
				bool fresh = NewDraftState(app, previousPage, page,
					!previousEditor.SequenceEqual(editor.GetRuntimeId()), previousHistory,
					history, draft) || (app == "ChatGPT" && (page.StartsWith("app://") || page.StartsWith("https://chatgpt.com/")) &&
					ConversationRoute.Canonical(app,page) == null && newSurface && !history && !draft);
				string newState = "page=" + page + " history=" + history + " draft=" + draft + " newSurface=" + newSurface +
					" fresh=" + fresh + " editor=" + Name(editor) + " help=" + editor.Current.HelpText + " valueChars=" + Clean(Value(editor)).Length;
				if (newState != lastNewState) { DropTrace.Write(app,"new-chat-state " + newState); lastNewState = newState; }
				stable = fresh ? stable + 1 : 0;
				return stable >= 2 ? "ready" : null;
			}, 12000);
			if (confirmed == null) throw new InvalidOperationException("未能确认空白新聊天已打开，文件尚未加入。请打开目标新聊天，再拖入文件并选择「放入当前对话」。");
		}

		internal static bool NewDraftState(string app, string previousPage, string page,
			bool editorChanged, bool previousHistory, bool history, bool draft)
		{
			if (history || draft) return false;
			Uri uri;
			if (!Uri.TryCreate(page, UriKind.Absolute, out uri)) return false;
			if (app == "Claude") return uri.Host == "claude.ai" && uri.AbsolutePath.TrimEnd('/') == "/new";
			if (app != "ChatGPT" || !(uri.Scheme == "app" || uri.Host == "chatgpt.com")) return false;
			if (page != previousPage) return true;
			// Existing conversation URLs must actually change before files may be added.
			if (ConversationRoute.Canonical(app, page) != null) return false;
			return editorChanged || previousHistory || uri.AbsolutePath.Trim('/') == "new" ||
				(uri.Host == "chatgpt.com" && uri.AbsolutePath.Trim('/').Length == 0);
		}

		internal bool FreshConversation(Handoff j)
		{
			if ((!j.NewChat && (!j.PairChats || j.SourceRoute == null)) || j.FreshReady)
			{
				return true;
			}
			Guard();
			AutomationElement editor = Editor();
			EmptyOrOwned(editor, "");
			Uri result;
			bool flag = j.To == "Claude" && Uri.TryCreate(PageKey(), UriKind.Absolute, out result) && result.Host == "claude.ai" && result.AbsolutePath.TrimEnd('/') == "/new";
			if (!flag && j.To == "ChatGPT" && j.RequestedNew)
			{
				flag = !Of(Root, ControlType.Button).Any((AutomationElement b) => !b.Current.IsOffscreen && Match(Name(b), "^(复制回复|複製回覆|Copy response|Edit message|编辑消息|编辑提示)$"));
			}
			if (flag)
			{
				j.FreshReady = true;
				RelayStore.Save(j);
				return true;
			}
			if (j.RequestedNew)
			{
				if (DateTime.UtcNow.Ticks - j.Updated > TimeSpan.FromSeconds(18.0).Ticks)
				{
					throw new InvalidOperationException("请在接收方打开一个空白新聊天，再点继续。");
				}
				return false;
			}
			if (HasDraftOrAttachment())
			{
				throw new InvalidOperationException("接收方有未发送的草稿或附件，后台会在输入框清空后继续。");
			}
			j.RequestedNew = true;
			RelayStore.Save(j);
			if (j.To == "Claude")
			{
				AutomationElement automationElement = Of(Root, ControlType.Hyperlink).FirstOrDefault(delegate(AutomationElement e)
				{
					ValuePattern valuePattern = Pattern<ValuePattern>(e, ValuePattern.Pattern);
					Uri result2;
					return valuePattern != null && Uri.TryCreate(valuePattern.Current.Value, UriKind.Absolute, out result2) && result2.Host == "claude.ai" && result2.AbsolutePath.TrimEnd('/') == "/new";
				});
				AutomationElement automationElement2 = automationElement ?? Of(Root, ControlType.Button).FirstOrDefault((AutomationElement e) => Match(Name(e), "^(新建聊天|新聊天|新对话|新增對話|New chat|Start new chat)(?:\\s*[\\(（].*)?$"));
				if (automationElement2 != null)
				{
					Act(automationElement2);
				}
				else
				{
					ProcessStartInfo processStartInfo = new ProcessStartInfo("claude://claude.ai/new?surface=chat&source=desktop_action");
					processStartInfo.UseShellExecute = true;
					Process.Start(processStartInfo);
				}
			}
			else
			{
				AutomationElement e2 = Of(Root, ControlType.Button).Concat(Of(Root, ControlType.Hyperlink)).FirstOrDefault((AutomationElement e) => !e.Current.IsOffscreen && Match(Name(e), "^(新聊天|新建聊天|New chat)(\\s*[\\(（].*)?$"));
				Act(e2);
			}
			return false;
		}

		internal void VerifyAttachment(Handoff j)
		{
			CheckRoute();
			if ((!j.Returning && ExpectedRoute == null && !string.IsNullOrEmpty(j.TargetPage) && PageKey() != j.TargetPage) || !HasFile(j.SendingName))
			{
				throw new InvalidOperationException("目标聊天或附件已变化，请核对后继续。");
			}
		}

		internal void Attach(Handoff j)
		{
			Guard();
			AutomationElement automationElement = FileDialog();
			AutomationElement automationElement2 = ChooseLocal();
			AutomationElement next = ((j.UploadStarted && automationElement == null && automationElement2 == null) ? UploadItem() : null);
			if (!j.UploadStarted && (automationElement != null || automationElement2 != null))
			{
				throw new InvalidOperationException("请先关闭已有的文件选择框，再继续。");
			}
			if (automationElement == null && automationElement2 == null)
			{
				AutomationElement automationElement3 = Editor();
				EmptyOrOwned(automationElement3, j.ReceiverPrompt());
				if (j.UploadStarted && HasFile(j.SendingName))
				{
					return;
				}
				AutomationElement automationElement4 = ComposerArea(automationElement3);
				if (automationElement4 == null)
				{
					throw new InvalidOperationException("未识别到聊天附件控件。");
				}
				if (HasAttachments(automationElement3))
				{
					throw new InvalidOperationException("目标输入框已有附件。请清空后继续。");
				}
				if (!j.UploadStarted)
				{
					j.UploadStarted = true;
					RelayStore.Save(j);
				}
			}
			PickAttachment(j.Returning ? RelayStore.ResultFile(j) : RelayStore.FileOf(j), automationElement, automationElement2, next);
			if (Until(() => (!HasFile(j.SendingName)) ? null : "attached", 16000) == null)
			{
				throw new InvalidOperationException("未确认附件已加入输入框。请检查目标聊天后继续。");
			}
		}

		internal void DismissDraftMenu()
		{
			Guard();
			if (!Native.IsWindowEnabled(Window))
				throw new InvalidOperationException("目标聊天有尚未关闭的对话框。");
			// Recover the open Add menu left by earlier versions. A normal drag
			// never opens this menu and never launches a file picker.
			var add = ComposerControl(Editor(), IsAdd);
			var menu = Pattern<ExpandCollapsePattern>(add, ExpandCollapsePattern.Pattern);
			if (menu != null && menu.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
			{
				menu.Collapse();
			}
		}

		internal void DropDraftFiles(string[] paths, Action beforeDrop)
		{
			Guard();
			var bounds = Editor().Current.BoundingRectangle;
			if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
				throw new InvalidOperationException("聊天输入框尚未显示。");
			int x = (int)Math.Floor(bounds.X + bounds.Width / 2);
			int y = (int)Math.Floor(bounds.Y + bounds.Height / 2);
			DropTrace.Write("ui", "native-drop-start files=" + paths.Length);
			ComposerFileDrop.DeliverTracked(Window,x,y,paths,delegate { Guard(); }, beforeDrop);
		}

		private void PickAttachment(string path, AutomationElement dialog, AutomationElement chooser, AutomationElement next)
		{
			if (dialog == null && chooser == null)
			{
				if (next == null) next = UploadItem();
				if (next == null)
				{
					ActFresh(() => ComposerControl(Editor(), IsAdd));
					next = Until(() => FileDialog() ?? ChooseLocal() ?? UploadItem(), 3500);
				}
				if (next == null)
				{
					throw new InvalidOperationException("未找到客户端的上传文件入口。");
				}
				dialog = FileDialog();
				chooser = dialog == null ? ChooseLocal() : null;
				if (dialog == null && chooser == null)
				{
					ActFresh(() => UploadItem() ?? ChooseLocal());
					Until(() => FileDialog() ?? ChooseLocal(), 6500);
					dialog = FileDialog();
					chooser = dialog == null ? ChooseLocal() : null;
				}
			}
			if (dialog == null && chooser != null)
			{
				ActFresh(() => ChooseLocal());
				dialog = Until(() => FileDialog(), 6500);
			}
			if (dialog == null)
			{
				throw new InvalidOperationException("客户端未打开文件选择框。");
			}
			SelectFile(dialog, path, false);
			DropTrace.Write("ui", "file-dialog-submitted");
		}

		internal void SelectFile(AutomationElement dialog, string path, bool saving)
		{
			if (CheckDraftTarget == null || saving)
			{
				SelectFileCore(dialog, path, saving);
				return;
			}
			// Opening a native picker can replace the disabled Electron accessibility
			// tree. Reacquire the owned picker rather than retain its initial UIA node.
			if (Until(delegate
			{
				AutomationElement fresh = FileDialog();
				if (fresh == null) return null;
				SelectFileCore(fresh, path, false);
				return "selected";
			}, 6500) == null) throw new InvalidOperationException("文件选择框尚未就绪，文件未选入。");
		}

		private void SelectFileCore(AutomationElement dialog, string path, bool saving)
		{
			Guard(CheckDraftTarget != null && !saving);
			DropTrace.Write("ui", "picker-select-start");
			AutomationElement automationElement = dialog.FindFirst(Desc, new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"));
			AutomationElement automationElement2 = ((automationElement.Current.ControlType == ControlType.Edit) ? automationElement : Of(automationElement, ControlType.Edit).FirstOrDefault());
			ValuePattern valuePattern = Pattern<ValuePattern>(automationElement2 ?? automationElement, ValuePattern.Pattern);
			if (valuePattern == null)
			{
				throw new InvalidOperationException("无法设置附件路径。");
			}
			if (saving)
			{
				string fileName = Path.GetFileName((valuePattern.Current.Value ?? "").Trim('"'));
				if (!string.Equals(fileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("另存为窗口的文件名与 GPT 附件不符，已暂停下载。");
				}
			}
			valuePattern.SetValue(path);
			DropTrace.Write("ui", "picker-path-set");
			if (!string.Equals(valuePattern.Current.Value.Trim('"'), path, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("附件路径校验失败。");
			}
			// Current Windows file pickers can expose the native Open/Save command
			// as a Pane rather than a Button. File-list items may also have id 1,
			// so retain the command label/role check when resolving that id.
			string commitName = saving ? "^(保存|儲存|Save)(\\s*[\\(（].*)?$" : "^(打开|開啟|Open)(\\s*[\\(（]&?[Oo][\\)）])?$";
			AutomationElement[] commitControls = dialog.FindAll(Desc, new OrCondition(new Condition[] {
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
				new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane) })).Cast<AutomationElement>().ToArray();
			AutomationElement automationElement3 = commitControls.FirstOrDefault(e => e.Current.AutomationId == "1" && Match(Name(e), commitName));
			if (automationElement3 == null)
			{
				automationElement3 = commitControls.FirstOrDefault(e => Match(Name(e), commitName));
			}
			if (automationElement3 == null)
			{
				DropTrace.Write("ui", "picker-buttons=" + string.Join("|", dialog.FindAll(Desc,Condition.TrueCondition).Cast<AutomationElement>()
					.Where(e => e.Current.ControlType == ControlType.Button || e.Current.ControlType == ControlType.SplitButton || e.Current.AutomationId == "1" || Name(e).Contains("打开"))
					.Select(e => e.Current.ControlType.ProgrammaticName + "/" + e.Current.AutomationId + "/" + Name(e))));
				throw new InvalidOperationException("未找到文件选择框的打开按钮。");
			}
			AutomationElement automationElement4 = FileDialog(saving);
			if (automationElement4 == null || !Automation.Compare(automationElement4, dialog))
			{
				throw new InvalidOperationException("文件选择框已关闭。");
			}
			InvokePattern invokePattern = Pattern<InvokePattern>(automationElement3, InvokePattern.Pattern);
			if (CheckDraftTarget != null)
			{
				Guard(!saving);
			}
			if (invokePattern != null) invokePattern.Invoke();
			else if (!UiaAction.TryInvokeDefault(new IntPtr(dialog.Current.NativeWindowHandle),automationElement3.GetRuntimeId()))
			{
				IntPtr dialogHandle = new IntPtr(dialog.Current.NativeWindowHandle);
				IntPtr command = Native.GetDlgItem(dialogHandle, 1);
				var commandName = new StringBuilder(100);
				var commandClass = new StringBuilder(100);
				Native.GetWindowText(command, commandName, commandName.Capacity);
				Native.GetClassName(command, commandClass, commandClass.Capacity);
				if (command == IntPtr.Zero || !Native.IsChild(dialogHandle,command) ||
					commandClass.ToString() != "Button" || !Match(commandName.ToString().Replace("&", ""),commitName))
					throw new InvalidOperationException("无法确认选定文件。");
				// IDOK/BN_CLICKED is the native file dialog's own commit command.
				// Unlike a synthetic mouse click it also works behind another app.
				if (!Native.PostMessage(dialogHandle,0x0111,new IntPtr(1),command))
					throw new InvalidOperationException("文件选择框未接受打开命令。");
				DropTrace.Write("ui", "picker-native-command-posted");
			}
			DropTrace.Write("ui", "picker-open-invoked");
		}

		internal void Send(string prompt, Action beforeClick)
		{
			Guard();
			if (DraftDrop.Protects(this))
			{
				throw new InvalidOperationException("当前草稿中有手动拖入的附件，请先发送或移除。");
			}
			AutomationElement automationElement = Editor();
			EmptyOrOwned(automationElement, prompt);
			ValuePattern valuePattern = Pattern<ValuePattern>(automationElement, ValuePattern.Pattern);
			if (valuePattern == null || valuePattern.Current.IsReadOnly)
			{
				throw new InvalidOperationException("聊天输入框暂时不可写。");
			}
			if (!SameText(Value(automationElement), prompt))
			{
				valuePattern.SetValue(prompt);
			}
			if (Until(() => (!SameText(Value(Editor()), prompt)) ? null : "accepted", 3000) == null)
			{
				throw new InvalidOperationException("客户端没有接受完整的交接说明。");
			}
			AutomationElement automationElement2 = Until(() => ComposerControl(Editor(), IsSend), 10000);
			if (automationElement2 == null)
			{
				throw new InvalidOperationException("客户端尚未启用发送按钮。请检查上传或登录状态。");
			}
			Guard();
			if (!SameText(Value(Editor()), prompt))
			{
				throw new InvalidOperationException("输入内容已变化，请核对草稿。");
			}
			beforeClick();
			Act(automationElement2);
		}

		internal bool Posted(string marker)
		{
			if (marker.StartsWith("双窗回传 ") && GptTranscript.Read(Window).Turns.Any((GptTurn t) => t.Prompt.Contains(marker)))
			{
				return true;
			}
			AutomationElement editor;
			try
			{
				editor = Editor();
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			if (DraftText(editor).Contains(marker))
			{
				return false;
			}
			return TranscriptContains(marker);
		}

		internal bool TranscriptContains(string marker)
		{
			if (!Of(Root, ControlType.Document).Any(delegate(AutomationElement d)
			{
				TextPattern textPattern = Pattern<TextPattern>(d, TextPattern.Pattern);
				return textPattern != null && textPattern.DocumentRange.FindText(marker, false, false) != null;
			}))
			{
				return Of(Root, ControlType.Text).Any((AutomationElement e) => Name(e).Contains(marker));
			}
			return true;
		}

		internal string GeneratedFile(Handoff j)
		{
			return MarkedBody(j.Begin, j.End);
		}

		internal string MarkedBody(string first, string last)
		{
			foreach (AutomationElement item in Of(Root, ControlType.Document).Reverse())
			{
				TextPattern textPattern = Pattern<TextPattern>(item, TextPattern.Pattern);
				if (textPattern == null)
				{
					continue;
				}
				TextPatternRange documentRange = textPattern.DocumentRange;
				if (documentRange.FindText(first, false, false) != null)
				{
					string text = ExtractMarked(documentRange.GetText(-1), first, last);
					if (text != null)
					{
						return text;
					}
				}
			}
			foreach (AutomationElement item2 in Of(Root, ControlType.Text))
			{
				string text2 = item2.Current.Name ?? "";
				string text3 = ExtractMarked(text2, first, last);
				if (text3 != null)
				{
					return text3;
				}
			}
			return null;
		}

		private static string ExtractMarked(string text, string first, string last)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			text = text.Replace("\r\n", "\n").Replace('\r', '\n');
			int num = text.IndexOf(first + "\n", StringComparison.Ordinal);
			if (num < 0)
			{
				return null;
			}
			num += first.Length + 1;
			int num2 = text.IndexOf("\n" + last, num, StringComparison.Ordinal);
			if (num2 < 0)
			{
				return null;
			}
			string text2 = text.Substring(num, num2 - num);
			if (text2.Length > 1000000)
			{
				throw new InvalidOperationException("生成的文本超过 100 万字符，请改用已有文件转发。");
			}
			if (string.IsNullOrWhiteSpace(text2))
			{
				throw new InvalidOperationException("写作方返回了空文件。");
			}
			return text2;
		}
	}
}
