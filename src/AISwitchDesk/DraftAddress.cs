using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace AISwitchDesk
{
    // A generic /new or app:// page can represent multiple conversations.
    // A turn fingerprint, or the blank editor identity, distinguishes them.
    internal sealed class DraftAddress
    {
        public string Page, Turn;
        public long Window;
        public int[] BlankEditor;

        internal static DraftAddress Capture(ChatUi ui, string app)
        {
            // Chromium can replace its accessibility tree during a drag. All
            // attempts here are read-only, before any native file delivery.
            for (int attempt = 0; ; attempt++)
            {
                try { return Read(ui, app); }
                catch (ElementNotAvailableException) { if (attempt == 5) throw; }
                catch (COMException) { if (attempt == 5) throw; }
                catch (InvalidOperationException) { if (attempt == 5) throw; }
                Thread.Sleep(140);
            }
        }

        private static DraftAddress Read(ChatUi ui, string app)
        {
            var page = ui.PageKey();
            if (string.IsNullOrEmpty(page)) throw new InvalidOperationException("请先在 " + app + " 打开聊天。");
            string turn = app == "ChatGPT" && DraftDrop.GenericGptPage(page) ? DraftDrop.TurnOf(ui) : null;
            bool blank = ConversationRoute.Canonical(app, page) == null && string.IsNullOrEmpty(turn);
            return new DraftAddress { Page = page, Turn = turn, Window = ui.Window.ToInt64(),
                BlankEditor = blank ? ui.Editor().GetRuntimeId() : null };
        }

        internal void RequireSame(DraftAddress current, string app)
        {
            if (!Matches(current))
                throw new InvalidOperationException("聊天已切换，请回到拖入时的 " + app + " 对话再重试。");
        }

        internal bool Matches(DraftAddress other)
        {
            return other != null && Page == other.Page && Turn == other.Turn &&
                (BlankEditor == null ? other.BlankEditor == null :
                    Window == other.Window && other.BlankEditor != null && BlankEditor.SequenceEqual(other.BlankEditor));
        }
    }
}
