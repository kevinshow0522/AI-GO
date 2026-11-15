using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace AISwitchDesk
{
    // "Ask both": one prompt into the current ChatGPT and Claude conversations.
    internal sealed partial class DeskForm
    {
        private AskBox askBox;
        private bool asking;

        private void HideAsk()
        {
            if (askBox != null) askBox.Hide();
        }

        private void OpenAsk()
        {
            if (closing) return;
            CloseExitMenu();
            HideSettings();
            usagePopup.Close();
            if (askBox == null)
            {
                askBox = new AskBox(this);
                askBox.VisibilityChanged += delegate
                {
                    if (!closing) buttons[(int)RailAction.Ask].Selected = askBox.Visible;
                };
            }
            if (askBox.Visible)
            {
                askBox.Hide();
                return;
            }
            askBox.Show(PopupPosition(askBox.Size, buttons[(int)RailAction.Ask].Top));
        }

        // Puts the windows side by side if asked, then sends. Both composers are
        // checked before either is written to: if one holds an unsent draft or
        // attachment, nothing is sent, so the two chats never get out of step.
        internal async Task<AskResult> AskBoth(string prompt, bool sideBySide)
        {
            if (asking) return new AskResult { Message = "上一条还在发送。" };
            if (Uploads.Active != null) return new AskResult { Message = "正在加入文件，加完后再发送。" };
            if (relay.Busy) return new AskResult { Message = "正在处理一条交接，稍后再发送。" };
            asking = true;
            try
            {
                IntPtr[] windows = await Task.WhenAll(RequireWindow(0), RequireWindow(1));
                if (closing) return new AskResult { Message = "侧栏正在退出。" };
                if (sideBySide && !comparison.Active && !arranging)
                {
                    comparison.Arrange(windows[0], windows[1], Screen.FromHandle(base.Handle));
                    UpdateComparisonButton();
                }
                else
                {
                    // A minimized window shows no chat to type into.
                    foreach (IntPtr window in windows)
                        if (Native.IsIconic(window)) Native.ShowWindowAsync(window, 4);
                }
                AskResult result = await Task.Run(() => Deliver(prompt, windows));
                DropTrace.Write("ask", result.AllSent ? "sent to both" : "stopped: " + result.Message);
                return result;
            }
            catch (Exception error)
            {
                return new AskResult { Message = error.GetBaseException().Message };
            }
            finally
            {
                asking = false;
            }
        }

        private static AskResult Deliver(string prompt, IntPtr[] windows)
        {
            string[] apps = { "ChatGPT", "Claude" };
            var views = new ChatUi[2];
            for (int i = 0; i < 2; i++)
            {
                IntPtr window = windows[i];
                string app = apps[i];
                views[i] = new ChatUi(window, () => Native.IsWindow(window) && BackgroundRelay.IsClient(window, app));
                try
                {
                    views[i].WaitForEditor();
                    if (views[i].HasDraftOrAttachment())
                        return new AskResult { Message = app + " 的输入框里有还没发送的内容。两边都没有发送，处理后再试。" };
                }
                catch (Exception error)
                {
                    if (!(error is InvalidOperationException) && !(error is ElementNotAvailableException)) throw;
                    return new AskResult { Message = app + "：" + error.Message + " 两边都没有发送。" };
                }
            }
            var sent = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                ChatUi view = views[i];
                string failure = null;
                try
                {
                    if (!ClientInput.TryUse(apps[i], delegate { view.Send(prompt, delegate { }); }))
                        failure = "正在后台处理文件，稍后再试";
                }
                catch (Exception error)
                {
                    if (!(error is InvalidOperationException) && !(error is ElementNotAvailableException)) throw;
                    failure = error.Message;
                }
                if (failure != null)
                {
                    string done = sent.Count == 0 ? "两边都没有发送。" : string.Join("、", sent) + " 已经发送。";
                    return new AskResult { Message = apps[i] + " 没有发送：" + failure.TrimEnd('。') + "。" + done };
                }
                sent.Add(apps[i]);
            }
            return new AskResult { AllSent = true, Message = "已发送到 ChatGPT 和 Claude。" };
        }
    }
}
