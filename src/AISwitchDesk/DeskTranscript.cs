using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AISwitchDesk
{
    // "对话传给…" in an app icon's menu: the last exchange of the conversation
    // that app is showing becomes a Markdown file in the other app's input box.
    // Nothing is sent;
    // the person adds what they want to ask about it and sends it themselves.
    internal sealed partial class DeskForm
    {
        private bool exporting;

        private async void SendConversation(int from)
        {
            if (closing || exporting) return;
            int to = 1 - from;
            string name = clients[from].Name;
            IntPtr window = clients[from].FindMainWindow();
            if (window == IntPtr.Zero)
            {
                Notice(from, name + " 没有打开，读不到对话。");
                return;
            }
            exporting = true;
            string path = null, problem = null;
            int count = 0;
            Notice(from, "正在读取 " + name + " 的对话…");
            try
            {
                await Task.Run(delegate
                {
                    // Only the last exchange goes across: the latest message and its reply.
                    List<ChatTurn> turns = Transcript.LastExchange(Transcript.Read(name, window));
                    count = turns.Count;
                    if (count > 0) path = Transcript.Save(name, turns);
                });
            }
            catch (Exception error)
            {
                // Whatever went wrong, the person is told and the sidebar carries on.
                problem = error.GetBaseException().Message;
                DropTrace.Write("transcript", name + " failed " + error.GetBaseException().GetType().Name);
            }
            finally
            {
                exporting = false;
            }
            if (closing) return;
            if (problem != null || path == null)
            {
                Notice(from, problem ?? (name + " 当前页面没有读到对话内容。打开要传的对话再试。"));
                return;
            }
            DropTrace.Write("transcript", name + " messages=" + count + " to " + clients[to].Name);
            QueueFiles(to, new[] { path }, false);
            Notice(to, "已把 " + name + " 的最后一轮对话存成文件，正在放进 " + clients[to].Name + " 的输入框。补一句想问的再发送。");
        }
    }
}
