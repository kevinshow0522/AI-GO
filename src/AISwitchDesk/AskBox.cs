using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;

namespace AISwitchDesk
{
    // The small window behind the rail's "ask both" button: one text box whose
    // contents go to ChatGPT and Claude at once, in a new conversation on each
    // side unless that is switched off.
    internal sealed class AskBox : IDisposable
    {
        private readonly DeskForm desk;
        private readonly Window window;
        private readonly System.Windows.Controls.TextBox prompt;
        private readonly TextBlock hint, status, sendLabel, scope;
        private readonly System.Windows.Controls.Button send;
        private readonly System.Windows.Controls.CheckBox arrange, fresh;
        private readonly OutsideClick outside;
        private readonly IntPtr handle;
        private bool sending, disposed;

        internal event Action VisibilityChanged;

        internal bool Visible
        {
            get { return !disposed && window.IsVisible; }
        }

        internal AskBox(DeskForm owner)
        {
            desk = owner;
            using (Stream stream = typeof(AskBox).Assembly.GetManifestResourceStream("Ask.xaml"))
            {
                window = (Window)XamlReader.Load(stream);
            }
            prompt = (System.Windows.Controls.TextBox)window.FindName("Prompt");
            hint = (TextBlock)window.FindName("Hint");
            status = (TextBlock)window.FindName("Status");
            sendLabel = (TextBlock)window.FindName("SendLabel");
            send = (System.Windows.Controls.Button)window.FindName("Send");
            arrange = (System.Windows.Controls.CheckBox)window.FindName("Arrange");
            fresh = (System.Windows.Controls.CheckBox)window.FindName("Fresh");
            scope = (TextBlock)window.FindName("Scope");
            RoutedEventHandler choice = delegate { Refresh(); };
            fresh.Checked += choice;
            fresh.Unchecked += choice;
            ApplyTheme();
            handle = new WindowInteropHelper(window) { Owner = owner.Handle }.EnsureHandle();
            // A click outside closes the box, but not while it is sending: arranging
            // the windows moves them under the pointer.
            outside = new OutsideClick(root => root == handle || root == desk.Handle, delegate { if (!sending) Hide(); });
            ElementHost.EnableModelessKeyboardInterop(window);
            window.IsVisibleChanged += delegate
            {
                if (window.IsVisible) outside.Watch();
                else outside.Dispose();
                if (VisibilityChanged != null) VisibilityChanged();
            };
            window.Closing += delegate(object sender, CancelEventArgs e)
            {
                if (!disposed)
                {
                    e.Cancel = true;
                    window.Dispatcher.BeginInvoke(new Action(Hide));
                }
            };
            window.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == Key.Escape && !sending)
                {
                    e.Handled = true;
                    Hide();
                }
            };
            prompt.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    SendNow();
                }
            };
            prompt.TextChanged += delegate { Refresh(); };
            send.Click += delegate { SendNow(); };
            Refresh();
        }

        internal void ApplyTheme()
        {
            DeskTheme.Current.ApplyResources(window.Resources);
        }

        internal void Show(System.Drawing.Point location)
        {
            double scale = Math.Max(96u, Native.GetDpiForWindow(desk.Handle)) / 96.0;
            Native.SetWindowPos(handle, IntPtr.Zero, location.X, location.Y,
                (int)Math.Round(window.Width * scale), (int)Math.Round(window.Height * scale), 20u);
            if (!sending) Status(null);
            if (!window.IsVisible) window.Show();
            window.Activate();
            prompt.Focus();
            prompt.SelectAll();
        }

        internal System.Drawing.Size Size
        {
            get
            {
                double scale = Math.Max(96u, Native.GetDpiForWindow(desk.Handle)) / 96.0;
                return new System.Drawing.Size((int)Math.Round(window.Width * scale), (int)Math.Round(window.Height * scale));
            }
        }

        internal void Hide()
        {
            if (!disposed && window.IsVisible) window.Hide();
        }

        private void Refresh()
        {
            hint.Visibility = prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            send.IsEnabled = !sending && prompt.Text.Trim().Length > 0;
            prompt.IsEnabled = !sending;
            arrange.IsEnabled = !sending;
            fresh.IsEnabled = !sending;
            scope.Text = fresh.IsChecked == true ? "两边各新开一个对话" : "发到两边当前的对话";
            sendLabel.Text = sending ? "正在发送…" : "发送到两边";
        }

        private void Status(string text)
        {
            status.Text = text ?? "";
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void SendNow()
        {
            string text = prompt.Text.Trim();
            if (sending || text.Length == 0) return;
            sending = true;
            Status(null);
            Refresh();
            AskResult result;
            try
            {
                result = await desk.AskBoth(text, arrange.IsChecked == true, fresh.IsChecked == true);
            }
            finally
            {
                sending = false;
            }
            if (disposed) return;
            Refresh();
            if (result.AllSent)
            {
                prompt.Clear();
                Hide();
                return;
            }
            // Keep the text, so nothing typed is lost, and say what happened.
            Status(result.Message);
            if (!window.IsVisible) window.Show();
            window.Activate();
            prompt.Focus();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            outside.Dispose();
            window.Close();
        }
    }

    internal sealed class AskResult
    {
        internal bool AllSent;
        internal string Message;
    }
}
