using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace AISwitchDesk
{
    internal enum CheckState { Good, Warn, Bad, Skipped }

    internal sealed class CheckLine
    {
        internal string Label, Detail;
        internal CheckState State;

        internal CheckLine(string label, CheckState state, string detail)
        {
            Label = label;
            State = state;
            Detail = detail;
        }
    }

    // Looks, without touching anything, at whether the sidebar can still find
    // what it works with in each client: the window, the page, the input box and,
    // for ChatGPT, the conversation text the background relay reads. Client
    // updates change these, and this says which link broke.
    internal static class ConnectionCheck
    {
        internal static List<CheckLine> Run(string app, IntPtr window)
        {
            var lines = new List<CheckLine>();
            if (window == IntPtr.Zero || !Native.IsWindow(window))
            {
                lines.Add(new CheckLine("窗口", CheckState.Bad, "没有打开。打开 " + app + " 后再检查。"));
                return lines;
            }
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            string package = Native.PackageOf(pid);
            int cut = package.IndexOf('_');
            bool minimized = Native.IsIconic(window);
            lines.Add(new CheckLine("窗口", minimized ? CheckState.Warn : CheckState.Good,
                (cut > 0 ? package.Substring(0, cut) : app) + (minimized ? " · 已最小化，内容可能读不到，还原后再查" : " · 已打开")));
            bool ran = ClientInput.TryUse(app, delegate { Inspect(app, window, lines); });
            if (!ran) lines.Add(new CheckLine("内容", CheckState.Skipped, "正在处理文件，稍后再查。"));
            return lines;
        }

        private static void Inspect(string app, IntPtr window, List<CheckLine> lines)
        {
            var ui = new ChatUi(window, () => Native.IsWindow(window));
            string page = Probe(() => ui.PageKey());
            bool conversation = false;
            if (string.IsNullOrEmpty(page))
                lines.Add(new CheckLine("页面", CheckState.Bad, "读不到当前页面。客户端可能刚更新，或还没加载完。"));
            else
            {
                conversation = ConversationRoute.Canonical(app, page) != null;
                lines.Add(new CheckLine("页面", CheckState.Good, (conversation ? "已打开对话 · " : "新对话或其他页面 · ") + Shorten(page)));
            }

            string editorProblem = null;
            bool draft = false;
            try
            {
                ui.Editor();
                draft = ui.HasDraftOrAttachment();
            }
            catch (Exception error)
            {
                if (!Expected(error)) throw;
                editorProblem = error.Message;
            }
            if (editorProblem != null)
                lines.Add(new CheckLine("输入框", CheckState.Bad, editorProblem));
            else
                lines.Add(new CheckLine("输入框", draft ? CheckState.Warn : CheckState.Good,
                    draft ? "已找到 · 里面有未发送的内容，同时提问和交接会等它清空" : "已找到"));

            if (app != "ChatGPT") return;
            // The background relay reads the conversation to spot "hand this to Claude".
            int turns = -1;
            try { turns = GptTranscript.Read(window).Turns.Count; }
            catch (Exception error) { if (!Expected(error)) throw; }
            if (turns > 0)
                lines.Add(new CheckLine("对话内容", CheckState.Good, "读到 " + turns + " 轮，后台接力能识别指令"));
            else if (conversation)
                lines.Add(new CheckLine("对话内容", CheckState.Bad, "这个对话里读不到消息，后台接力识别不到「交给 Claude」的指令"));
            else
                lines.Add(new CheckLine("对话内容", CheckState.Skipped, "新对话还没有消息，发一条后再查"));
        }

        private static string Probe(Func<string> read)
        {
            try { return read(); }
            catch (Exception error)
            {
                if (!Expected(error)) throw;
                return null;
            }
        }

        // Failures that mean "could not see it", not a fault in the sidebar.
        private static bool Expected(Exception error)
        {
            return error is InvalidOperationException || error is ElementNotAvailableException || error is COMException
                || error is TimeoutException || error is ArgumentException;
        }

        private static string Shorten(string page)
        {
            Uri uri;
            if (!Uri.TryCreate(page, UriKind.Absolute, out uri)) return page;
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length > 22) path = path.Substring(0, 12) + "…" + path.Substring(path.Length - 6);
            return (uri.Scheme == "app" ? "app:" : uri.Host) + path;
        }
    }
}
