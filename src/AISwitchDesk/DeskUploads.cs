using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace AISwitchDesk
{
    internal sealed partial class DeskForm
    {
        private readonly Func<int, IntPtr> findUploadWindow;
        private readonly Func<int, Task<IntPtr>> openUploadWindow;

        private void QueueFiles(int target, string[] paths, bool fresh)
        {
            if (closing || paths == null || paths.Length == 0) return;
            bool both = target == (int)RailAction.Compare;
            CloseExitMenu();
            if (usagePopup.Visible) usagePopup.Close();
            if (!both) SetSelected(target);
            var destinations = new List<UploadDestination>();
            foreach (int index in both ? new[] { 0, 1 } : new[] { target })
            {
                var destination = new UploadDestination(index, fresh, both);
                destinations.Add(destination);
                // Record both current conversations at release, including the
                // recipient that will be processed second.
                IntPtr handle = findUploadWindow(index);
                if (!fresh && handle != IntPtr.Zero && !Native.IsIconic(handle))
                {
                    var ui = UploadChat(handle);
                    destination.Capture = CaptureDestination(destination, ui);
                }
            }
            DropTrace.Write("queue", "enqueue target=" + target + " new=" + fresh + " files=" + paths.Length);
            Uploads.Enqueue(destinations, paths);
        }

        private ChatUi UploadChat(IntPtr handle)
        {
            // ClientWindow already verified the app package when it acquired
            // this handle. Pin its process for every subsequent native action.
            uint owner;
            Native.GetWindowThreadProcessId(handle, out owner);
            return new ChatUi(handle, delegate {
                uint pid;
                return !closing && owner != 0 && Native.IsWindow(handle) &&
                    Native.GetWindowThreadProcessId(handle, out pid) != 0 && pid == owner;
            });
        }

        private async Task<DraftAddress> CaptureDestination(UploadDestination destination, ChatUi ui)
        {
            var address = await Task.Run(() => DraftAddress.Capture(ui, destination.App)).ConfigureAwait(false);
            OnUploadThread(delegate { destination.Address = address; Uploads.Checkpoint(); });
            return address;
        }

        private async Task<FileReceipt[]> DeliverUpload(UploadWork work)
        {
            var dest = work.Destination;
            IntPtr foreground = Native.GetForegroundWindow();
            IntPtr existing = findUploadWindow(dest.Client);
            bool waking = existing == IntPtr.Zero || Native.IsIconic(existing);
            Task<IntPtr> target = openUploadWindow(dest.Client);
            if (dest.Capture != null)
            {
                try { await dest.Capture; }
                finally { dest.Capture = null; }
            }
            IntPtr window = await target;
            var ui = UploadChat(window);
            var wait = Stopwatch.StartNew();
            while (!closing)
            {
                FileReceipt[] result = null;
                bool acquired = await Task.Run(() => ClientInput.TryUse(dest.App, delegate {
                    using (new BackgroundDrop(window, foreground))
                    {
                        if (waking || (dest.NewChat && !dest.NewOpened)) ui.WaitForEditor();
                        if (dest.NewChat && !dest.NewOpened)
                        {
                            ui.OpenNewDraft(dest.App);
                            OnUploadThread(delegate { dest.NewOpened = true; Uploads.Checkpoint(); });
                        }
                        var current = DraftAddress.Capture(ui, dest.App);
                        if (dest.Address != null) dest.Address.RequireSame(current, dest.App);
                        else OnUploadThread(delegate { dest.Address = current; Uploads.Checkpoint(); });
                        result = DraftDrop.Attach(ui, dest.App, work.Entries.Select(r => r.Path).ToArray(), dest.Address,
                            work.Entries.Select(r => r.Submitted).ToArray(), delegate(int[] indexes) {
                                OnUploadThread(delegate {
                                    foreach (int i in indexes) work.Entries[i].Submitted = true;
                                    Uploads.Checkpoint();
                                });
                            }, indexes => OnUploadThread(delegate { Uploads.Confirm(work, indexes); }));
                    }
                }));
                if (acquired) return result;
                if (wait.ElapsedMilliseconds >= 20000)
                    throw new InvalidOperationException(dest.App + " 仍在处理另一项文件操作。");
                await Task.Delay(300);
            }
            throw new OperationCanceledException("侧栏已退出。");
        }

        private void OnUploadThread(Action action)
        {
            if (closing || IsDisposed) throw new OperationCanceledException("侧栏已退出。");
            if (InvokeRequired) Invoke(action);
            else action();
        }

        private void OpenFileQueue()
        {
            if (closing) return;
            if (!SettingsOpen) OpenSettings();
            if (settings != null) settings.ShowFileQueue();
        }
    }
}
