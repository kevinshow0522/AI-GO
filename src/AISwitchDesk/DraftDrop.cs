using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal sealed class DraftDrop
	{
		private readonly ChatUi ui;

		private readonly string page;

		private readonly string turn;

		private int[] editorId;

		private readonly double[] editorBox;

		private readonly bool genericGpt;

		private DraftDrop(ChatUi chat, string app)
		{
			ui = chat;
			AutomationElement automationElement = chat.Editor();
			editorId = automationElement.GetRuntimeId();
			var bounds = automationElement.Current.BoundingRectangle;
			editorBox = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
			DropTrace.Write(app, "bound-composer box=" + bounds);
			page = chat.PageKey();
			if (string.IsNullOrEmpty(page))
			{
				throw new InvalidOperationException("请在目标应用中打开聊天，再拖入文件。");
			}
			genericGpt = app == "ChatGPT" && GenericGptPage(page);
			if (genericGpt)
			{
				turn = TurnOf(ui);
			}
		}

		internal static bool GenericGptPage(string key)
		{
			Uri result;
			if (Uri.TryCreate(key, UriKind.Absolute, out result) && (result.Scheme == "app" || result.Host == "chatgpt.com"))
			{
				return ConversationRoute.Canonical("ChatGPT", key) == null;
			}
			return false;
		}

		internal static string TurnOf(ChatUi chat)
		{
			for (int attempt = 0; attempt < 4; attempt++)
			{
				try
				{
					GptTurn gptTurn = GptTranscript.Read(chat.Window).Turns.LastOrDefault();
					return gptTurn == null ? "" : gptTurn.Key;
				}
				catch (ElementNotAvailableException) { if (attempt == 3) throw; }
				catch (COMException) { if (attempt == 3) throw; }
				Thread.Sleep(120);
			}
			return "";
		}

		private static string GuardFolder(string pageKey)
		{
			return Path.Combine(RelayStore.Root, "draft-attachments", RelayIntent.Fingerprint(pageKey));
		}

		private static string NamesFile(string folder, string turnKey)
		{
			return Path.Combine(folder, string.IsNullOrEmpty(turnKey) ? "composer.json" : (turnKey + ".json"));
		}

		private void Remember(string[] names)
		{
			string path = NamesFile(GuardFolder(page), turn);
			string[] source = (File.Exists(path) ? RelayStore.Json.Deserialize<string[]>(File.ReadAllText(path)) : new string[0]);
			string[] obj = source.Where(ui.HasDraftFile).Concat(names).Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			RelayStore.AtomicText(path, RelayStore.Json.Serialize(obj));
		}

		internal static bool Protects(ChatUi chat, bool includePending = true)
		{
			if (!Directory.Exists(Path.Combine(RelayStore.Root, "draft-attachments")))
			{
				return false;
			}
			string text = chat.PageKey();
			string text2 = GuardFolder(text);
			if (!Directory.Exists(text2))
			{
				return false;
			}
			string turnKey = (GenericGptPage(text) ? TurnOf(chat) : null);
			string path = NamesFile(text2, turnKey);
			if (!File.Exists(path))
			{
				return false;
			}
			string[] source = RelayStore.Json.Deserialize<string[]>(File.ReadAllText(path));
			if (source.Any(chat.HasDraftFile))
			{
				return true;
			}
			if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(30.0))
			{
				return includePending;
			}
			File.Delete(path);
			if (!Directory.EnumerateFileSystemEntries(text2).Any())
			{
				Directory.Delete(text2);
			}
			return false;
		}

		private void VerifyConversation()
		{
			if (ui.PageKey() != page || (genericGpt && TurnOf(ui) != turn))
			{
				throw new InvalidOperationException("聊天已切换，已停止添加文件。");
			}
		}

		private void Verify()
		{
			VerifyConversation();
			AutomationElement[] array = (from e in ChatUi.Of(ui.Root, ControlType.Edit)
				where !e.Current.IsOffscreen && (e.Current.AutomationId == "prompt-textarea" || e.Current.ClassName.IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0)
				select e).ToArray();
			if (!array.Any(e => editorId.SequenceEqual(e.GetRuntimeId())))
			{
				int rebound = ChatUi.ChooseEditorByBounds(array.Select(e => e.Current.BoundingRectangle.X).ToArray(),
					array.Select(e => e.Current.BoundingRectangle.Y).ToArray(),array.Select(e => e.Current.BoundingRectangle.Width).ToArray(),
					array.Select(e => e.Current.BoundingRectangle.Height).ToArray(),editorBox);
				if (rebound < 0) throw new InvalidOperationException("输入框已切换，已停止添加文件。");
				editorId = array[rebound].GetRuntimeId();
				ui.DraftEditorId = editorId;
				DropTrace.Write("ui", "composer-rebound-after-rerender");
			}
		}

		internal static FileReceipt[] Attach(ChatUi ui, string app, string[] paths, DraftAddress expected,
			bool[] previouslySubmitted, Action<int[]> beforeDrop, Action<int[]> confirmed)
		{
			var receipts = paths.Select(path => new FileReceipt()).ToArray();
			var accepted = new List<int>();
			DropTrace.Write(app, "attach-enter files=" + paths.Length);
			try
			{
				ui.DismissDraftMenu();
				var draft = new DraftDrop(ui, app);
				var address = new DraftAddress { Page = draft.page, Turn = draft.turn, Window = ui.Window.ToInt64(),
					BlankEditor = ConversationRoute.Canonical(app, draft.page) == null && string.IsNullOrEmpty(draft.turn) ? draft.editorId : null };
				if (expected != null) expected.RequireSame(address, app);
				ui.DraftEditorId = draft.editorId;
				ui.DraftEditorBox = draft.editorBox;
				ui.CheckDraftTarget = draft.Verify;
				if (RelayStore.All().Any(j => (j.Stage == "ready" || j.Stage == "confirm" || j.Returning) &&
					(j.Returning ? "ChatGPT" : j.To) == app && j.UploadStarted && j.SendingName != null && ui.HasFile(j.SendingName)))
					throw new InvalidOperationException("输入框中有正在交接的附件，请完成交接后再重试。");

				var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < paths.Length; i++)
				{
					string name = Path.GetFileName(paths[i]);
					if (!names.Add(name))
						receipts[i].Error = "同批文件重名，请改名后拖入。";
					else if (ui.HasDraftFile(name))
					{
						// A timed-out native drop can finish after the first check.
						if (previouslySubmitted != null && previouslySubmitted[i]) receipts[i].Added = true;
						else receipts[i].Error = "输入框已有同名附件，本次未重复加入。";
					}
					else if (!File.Exists(paths[i])) receipts[i].Error = "找不到可读取的本机文件。";
					else accepted.Add(i);
				}
				if (accepted.Count > 0)
				{
					var indexes = accepted.ToArray();
					var files = indexes.Select(i => paths[i]).ToArray();
					draft.Verify();
					// Recheck a timed-out single file at the native drop boundary;
					// a late chip is already a receipt for the original attempt.
					bool retrying = indexes.Length == 1 && previouslySubmitted != null && previouslySubmitted[indexes[0]];
					if (retrying && ChatUi.Until(() => ui.HasDraftFile(Path.GetFileName(files[0])) ? "ready" : null, 900) != null)
					{
						draft.VerifyConversation();
						receipts[indexes[0]].Added = true;
						return receipts;
					}
					ui.DropDraftFiles(files, delegate
					{
						if (retrying && ui.HasDraftFile(Path.GetFileName(files[0])))
						{
							draft.VerifyConversation();
							receipts[indexes[0]].Added = true;
							throw new OperationCanceledException("已确认原附件。");
						}
						foreach (int i in indexes) receipts[i].Submitted = true;
						if (beforeDrop != null) beforeDrop(indexes);
					});
					// A new chip can reflow the editor. Bind verification to the
					// conversation and inspect each filename independently.
					ui.DraftEditorId = null;
					ui.DraftEditorBox = null;
					try { draft.Remember(files.Select(Path.GetFileName).ToArray()); }
					catch (IOException e) { DropTrace.Write(app, "draft-cache: " + e.Message); }
					catch (UnauthorizedAccessException e) { DropTrace.Write(app, "draft-cache: " + e.Message); }
					ChatUi.Until(delegate
					{
						draft.VerifyConversation();
						var visible = indexes.Where(i => !receipts[i].Added && ui.HasDraftFile(Path.GetFileName(paths[i]))).ToArray();
						draft.VerifyConversation();
						foreach (int i in visible) receipts[i].Added = true;
						if (visible.Length != 0 && confirmed != null) confirmed(visible);
						return indexes.All(i => receipts[i].Added) ? "attached" : null;
					}, 12000);
					foreach (int i in indexes)
						if (!receipts[i].Added) receipts[i].Error = "输入框尚未确认这个附件。";
				}
				draft.VerifyConversation();
			}
			catch (Exception ex)
			{
				string error = ex.GetBaseException().Message;
				foreach (var receipt in receipts)
					if (!receipt.Added && receipt.Error == null) receipt.Error = error;
				DropTrace.Write(app, "attach-error " + error);
			}
			finally
			{
				ui.CheckDraftTarget = null;
				ui.DraftEditorId = null;
				ui.DraftEditorBox = null;
			}
			DropTrace.Write(app, "composer-attachments-confirmed=" + receipts.Count(r => r.Added) + "/" + paths.Length);
			return receipts;
		}
	}
}
