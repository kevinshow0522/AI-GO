using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace AISwitchDesk
{
    // One message of a conversation, as it reads on screen.
    internal sealed class ChatTurn
    {
        internal bool Assistant;
        internal string Text;
    }

    // Reads the conversation an app is showing and writes it out as a Markdown
    // file, so it can be handed to the other app as an attachment.
    internal static class Transcript
    {
        private const int MaxTurnChars = 600000;

        internal static List<ChatTurn> Read(string app, IntPtr window)
        {
            if (window == IntPtr.Zero || !Native.IsWindow(window))
            {
                throw new InvalidOperationException("未找到 " + app + " 窗口。");
            }
            return app == "Claude" ? ReadClaude(window) : ReadGpt(window);
        }

        // A page can show one message in several pieces, such as a reply's
        // working notes and then its answer. Pieces from the same side that
        // follow each other are kept as one message.
        private static void Add(List<ChatTurn> turns, bool assistant, string text)
        {
            text = Clean(text);
            if (!assistant) text = WithoutTime(text);
            // Spacers and dividers hold no letters or digits.
            if (!Regex.IsMatch(text, "[\\p{L}\\p{N}]")) return;
            ChatTurn last = turns.Count == 0 ? null : turns[turns.Count - 1];
            if (last != null && last.Assistant == assistant)
            {
                last.Text += "\n\n" + text;
                return;
            }
            turns.Add(new ChatTurn { Assistant = assistant, Text = text });
        }

        // A person's message is shown with the time it was sent on a line of its
        // own underneath, such as "13:45" or "昨天 下午 3:20". That line goes.
        internal static string WithoutTime(string text)
        {
            int cut = text.LastIndexOf('\n');
            if (cut < 0) return text;
            return IsTimeLine(text.Substring(cut + 1)) ? text.Substring(0, cut).TrimEnd() : text;
        }

        // A line that is nothing but a time, with or without its day.
        private static bool IsTimeLine(string line)
        {
            line = line.Trim();
            if (line.Length > 24 || !Regex.IsMatch(line, "\\d{1,2}[:：]\\d{2}")) return false;
            return Regex.Replace(line, "今天|昨天|前天|上午|下午|凌晨|早上|中午|晚上|星期.|周.|週.|年|月|日|today|yesterday|am|pm|at|[\\d\\s:：,，./\\-]", "", RegexOptions.IgnoreCase).Length == 0;
        }

        private const string GptLabels = "^(?:(?:ChatGPT|GPT|Codex)\\s*(?:[說说]|said)|你[說说]|您[說说]|You said|Assistant)\\s*[:：]$";

        // One-line notes ChatGPT leaves while it works. Several can run together on a line.
        private const string GptActivity = "^(?:已?运行了?命令|已搜索网页|已查看 ?\\d+ ?张图像|已优化对话|已使用 .{1,40}运行了命令|Ran (?:a )?commands?|Searched the web|Viewed \\d+ images?)+$";

        // What ChatGPT shows around a reply that is not the reply: labels for
        // screen readers, notes of commands run and pictures viewed, how long it
        // took, a reconnecting counter, and the "latest reply" button under it.
        internal static string WithoutActivity(string text)
        {
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            bool reconnecting = false;
            // True just after a note of work done, where a tool's panel may follow:
            // its name on a line, then what it returned as a block of code.
            bool working = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();
                // Everything from the "latest reply" button on belongs to the page, not the reply.
                if (Regex.IsMatch(line, "^(最新一条回复|跳至最新回复|Jump to latest|Scroll to bottom)$", RegexOptions.IgnoreCase)) break;
                if (Regex.IsMatch(line, "^(正在重新连接|Reconnecting)[.…]*$", RegexOptions.IgnoreCase))
                {
                    reconnecting = true;
                    continue;
                }
                if (line.Length == 0)
                {
                    kept.Add(raw);
                    continue;
                }
                // The counter under "reconnecting": "1" and "/5" on lines of their own.
                if (reconnecting && Regex.IsMatch(line, "^/?\\d+$")) continue;
                reconnecting = false;
                if (Regex.IsMatch(line, GptActivity, RegexOptions.IgnoreCase))
                {
                    working = true;
                    continue;
                }
                if (working)
                {
                    string next = "";
                    for (int j = i + 1; j < lines.Length && next.Length == 0; j++) next = lines[j].Trim();
                    bool block = Regex.IsMatch(line, CodeLabel, RegexOptions.IgnoreCase) || line == "{" || line == "[";
                    // A tool's name: short, not a sentence, and followed by more work or by its output.
                    bool title = !block && line.Length <= 40 && !Regex.IsMatch(line, "[。！？，；.!?,;:：]") &&
                        (Regex.IsMatch(next, GptActivity, RegexOptions.IgnoreCase) || Regex.IsMatch(next, CodeLabel, RegexOptions.IgnoreCase) || next == "{" || next == "[");
                    if (title) continue;
                    if (block)
                    {
                        // Skip the label, then the block up to the unindented bracket
                        // that closes it. Without one, only the label goes.
                        int start = (line == "{" || line == "[") ? i : i + 1;
                        string open = (start < lines.Length) ? lines[start].Trim() : "";
                        if (open == "{" || open == "[")
                        {
                            int end = Array.FindIndex(lines, start + 1, (string candidate) => candidate.TrimEnd() == ((open == "{") ? "}" : "]"));
                            if (end > 0) i = end;
                        }
                        working = false;
                        continue;
                    }
                }
                working = false;
                if (Regex.IsMatch(line, GptLabels, RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(line, "^(用时|Worked for|Thought for) .{1,30}$", RegexOptions.IgnoreCase) || IsTimeLine(line)) continue;
                kept.Add(raw);
            }
            return Clean(string.Join("\n", kept));
        }

        // The language named above a block of code.
        private const string CodeLabel = "^(json|text|plaintext|output|bash|shell|powershell|python|ruby|javascript|xml|yaml)$";

        // The last exchange: the person's most recent message and what followed it.
        internal static List<ChatTurn> LastExchange(List<ChatTurn> turns)
        {
            int start = turns.FindLastIndex((ChatTurn turn) => !turn.Assistant);
            if (start < 0) start = Math.Max(0, turns.Count - 1);
            return turns.GetRange(start, turns.Count - start);
        }

        // Text as the page gives it, without the stray characters and gaps.
        internal static string Clean(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("￼", "");
            text = Regex.Replace(text, "[ \\t]+\\n", "\n");
            text = Regex.Replace(text, "\\n{3,}", "\n\n");
            return text.Trim();
        }

        // The text between two elements of a page, laid out as the page lays it out.
        private static string Between(TextPattern page, AutomationElement first, AutomationElement last)
        {
            if (page == null || first == null || last == null) return null;
            try
            {
                TextPatternRange range = page.RangeFromChild(first);
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, page.RangeFromChild(last), TextPatternRangeEndpoint.End);
                string text = Clean(range.GetText(MaxTurnChars));
                return text.Length == 0 ? null : text;
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            return null;
        }

        // ChatGPT labels each turn for screen readers, which GptTranscript reads.
        private static List<ChatTurn> ReadGpt(IntPtr window)
        {
            GptTranscript shown = GptTranscript.Read(window);
            TextPattern page = null;
            foreach (AutomationElement document in ChatUi.Of(AutomationElement.FromHandle(window), ControlType.Document))
            {
                Uri uri = ChatUi.Page(document);
                if (uri != null && (uri.Scheme == "app" || uri.Host == "chatgpt.com"))
                {
                    page = ChatUi.Pattern<TextPattern>(document, TextPattern.Pattern) ?? page;
                }
            }
            var turns = new List<ChatTurn>();
            foreach (GptTurn turn in shown.Turns)
            {
                Add(turns, false, turn.Prompt);
                Add(turns, true, WithoutActivity(Between(page, turn.FirstAnswer, turn.LastAnswer) ?? turn.Body));
            }
            return turns;
        }

        // The row of actions under a message: it always offers "copy".
        private static bool IsMessageBar(AutomationElement bar)
        {
            return HasButton(bar, "^(复制|複製|Copy)$");
        }

        private static bool HasButton(AutomationElement bar, string names)
        {
            return ChatUi.Of(bar, ControlType.Button).Any((AutomationElement button) => Regex.IsMatch(ChatUi.Name(button), names, RegexOptions.IgnoreCase));
        }

        private static int Children(AutomationElement element)
        {
            int count = 0;
            for (AutomationElement child = TreeWalker.RawViewWalker.GetFirstChild(element); child != null; child = TreeWalker.RawViewWalker.GetNextSibling(child))
            {
                count++;
            }
            return count;
        }

        // The element whose children are the turns: the deepest one that holds
        // every message's action row.
        private static AutomationElement TurnList(AutomationElement page, AutomationElement first, AutomationElement last)
        {
            TreeWalker walker = TreeWalker.RawViewWalker;
            var above = new List<AutomationElement>();
            for (AutomationElement element = walker.GetParent(first); element != null; element = walker.GetParent(element))
            {
                above.Add(element);
                if (Automation.Compare(element, page)) break;
            }
            if (Automation.Compare(first, last))
            {
                // A single message. Its own wrappers hold one child each; the
                // list is the first element above them that holds more.
                for (int i = 1; i < above.Count; i++)
                {
                    if (Children(above[i]) > 1) return above[i];
                }
                return null;
            }
            for (AutomationElement element = walker.GetParent(last); element != null; element = walker.GetParent(element))
            {
                AutomationElement shared = above.FirstOrDefault((AutomationElement other) => Automation.Compare(other, element));
                if (shared != null) return shared;
                if (Automation.Compare(element, page)) break;
            }
            return null;
        }

        // Claude puts each message in a list, with a row of actions under it.
        // The person's own messages offer "edit"; Claude's do not.
        private static List<ChatTurn> ReadClaude(IntPtr window)
        {
            AutomationElement page = null;
            foreach (AutomationElement document in ChatUi.Of(AutomationElement.FromHandle(window), ControlType.Document))
            {
                Uri uri = ChatUi.Page(document);
                // The page itself comes first; pictures and tools embedded in it follow.
                if (uri != null && uri.Scheme == "https" && (uri.Host == "claude.ai" || uri.Host == "www.claude.ai"))
                {
                    page = document;
                    break;
                }
            }
            if (page == null)
            {
                throw new InvalidOperationException("没有读到 Claude 的页面。");
            }
            var turns = new List<ChatTurn>();
            TextPattern text = ChatUi.Pattern<TextPattern>(page, TextPattern.Pattern);
            AutomationElement[] bars = ChatUi.Of(page, ControlType.ToolBar).Where(IsMessageBar).ToArray();
            if (text == null || bars.Length == 0)
            {
                return turns;
            }
            AutomationElement list = TurnList(page, bars[0], bars[bars.Length - 1]);
            if (list == null)
            {
                return turns;
            }
            TreeWalker walker = TreeWalker.RawViewWalker;
            for (AutomationElement turn = walker.GetFirstChild(list); turn != null; turn = walker.GetNextSibling(turn))
            {
                try
                {
                    AutomationElement bar = ChatUi.Of(turn, ControlType.ToolBar).LastOrDefault(IsMessageBar);
                    TextPatternRange range = text.RangeFromChild(turn);
                    if (bar != null)
                    {
                        // The row of actions and the time beside it are not part of the message.
                        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, text.RangeFromChild(bar), TextPatternRangeEndpoint.Start);
                    }
                    // A reply still being written has no row of actions yet.
                    Add(turns, bar == null || !HasButton(bar, "^(编辑|編輯|Edit)$"), range.GetText(MaxTurnChars));
                }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch (ElementNotAvailableException) { }
            }
            return turns;
        }

        internal static string Markdown(string app, IList<ChatTurn> turns, DateTime when)
        {
            var text = new StringBuilder();
            text.Append("# ").Append(app).Append(" 对话记录\n\n");
            text.Append("由 AI 双窗于 ").Append(when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            text.Append(" 从 ").Append(app).Append(" 导出，是当前对话的最后一轮。\n");
            foreach (ChatTurn turn in turns)
            {
                text.Append("\n## ").Append(turn.Assistant ? app : "用户").Append("\n\n").Append(turn.Text).Append("\n");
            }
            return text.ToString();
        }

        // Writes the conversation next to the sidebar's other data and returns the file.
        internal static string Save(string app, IList<ChatTurn> turns)
        {
            string folder = Path.Combine(RelayStore.Root, "transcripts");
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder, "*.md"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-30.0)) File.Delete(old);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            DateTime now = DateTime.Now;
            string path = Path.Combine(folder, app + " 对话 " + now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ".md");
            File.WriteAllText(path, Markdown(app, turns, now), new UTF8Encoding(false));
            return path;
        }
    }
}
