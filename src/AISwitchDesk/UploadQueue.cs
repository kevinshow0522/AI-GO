using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AISwitchDesk
{
    internal enum UploadState { Waiting, Adding, Added, Failed }

    internal sealed class UploadDestination
    {
        internal readonly string Id;
        internal readonly int Client;
        internal readonly bool NewChat, FromCompare;
        internal bool NewOpened;
        internal DraftAddress Address;
        internal Task<DraftAddress> Capture;
        internal string App { get { return Client == 0 ? "ChatGPT" : "Claude"; } }

        internal UploadDestination(int client, bool fresh, bool both, string id = null)
        {
            Id = id ?? Guid.NewGuid().ToString("N");
            Client = client;
            NewChat = fresh;
            FromCompare = both;
        }
    }

    internal sealed class UploadEntry : INotifyPropertyChanged
    {
        internal readonly UploadDestination Destination;
        internal UploadState State;
        internal bool Submitted;
        internal string Detail = "";
        public string Path { get; private set; }
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        public bool CanRetry { get { return State == UploadState.Failed; } }
        public string Description { get { return Path + Environment.NewLine + Status; } }
        public string Status
        {
            get
            {
                string state = State == UploadState.Waiting ? "排队中" : State == UploadState.Adding ? "正在加入" :
                    State == UploadState.Added ? "已加入" : "待重试";
                return Destination.App + " · " + state + (Detail.Length == 0 ? "" : " · " + Detail);
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;

        internal UploadEntry(UploadDestination destination, string path)
        {
            Destination = destination;
            Path = path;
        }

        internal void Set(UploadState state, string detail)
        {
            State = state;
            Detail = detail ?? "";
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(""));
        }
    }

    internal sealed class UploadWork
    {
        internal readonly UploadDestination Destination;
        internal readonly UploadEntry[] Entries;
        internal UploadWork(UploadDestination destination, UploadEntry[] entries)
        {
            Destination = destination;
            Entries = entries;
        }
    }

    internal sealed class FileReceipt
    {
        internal bool Added, Submitted;
        internal string Error;
    }

    // Only the rail's UI thread mutates rows and advances the FIFO. The native
    // drop runs off-thread; its receipts come back in the original file order.
    internal sealed class UploadQueue
    {
        internal readonly ObservableCollection<UploadEntry> Entries = new ObservableCollection<UploadEntry>();
        private readonly Queue<UploadWork> pending = new Queue<UploadWork>();
        private readonly Func<UploadWork, Task<FileReceipt[]>> deliver;
        private readonly string journal;
        private readonly SynchronizationContext owner;
        internal UploadWork Active { get; private set; }
        internal volatile bool Stopped;
        internal event Action Changed;
        internal event Action<UploadWork> Finished;

        internal UploadQueue(Func<UploadWork, Task<FileReceipt[]>> delivery, string folder, SynchronizationContext owner)
        {
            deliver = delivery;
            this.owner = owner;
            journal = Path.Combine(folder, "upload-queue.json");
            Restore();
        }

        internal void Enqueue(IEnumerable<UploadDestination> destinations, string[] paths)
        {
            if (Stopped || paths == null) return;
            var files = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) return;
            var rows = new List<UploadEntry>();
            foreach (var destination in destinations)
            {
                var entries = files.Select(path => new UploadEntry(destination, path)).ToArray();
                rows.AddRange(entries);
                pending.Enqueue(new UploadWork(destination, entries));
            }
            for (int i = rows.Count - 1; i >= 0; i--) Entries.Insert(0, rows[i]);
            Publish();
            StartNext();
        }

        internal void Retry(UploadEntry row)
        {
            if (Stopped || row == null || !row.CanRetry || !Entries.Contains(row)) return;
            row.Set(UploadState.Waiting, "");
            pending.Enqueue(new UploadWork(row.Destination, new[] { row }));
            Publish();
            StartNext();
        }

        private void StartNext()
        {
            if (Stopped || Active != null || pending.Count == 0) return;
            Active = pending.Dequeue();
            foreach (var row in Active.Entries) row.Set(UploadState.Adding, "");
            Notify();
            DeliverNext(Active);
        }

        private async void DeliverNext(UploadWork work)
        {
            FileReceipt[] receipts;
            try
            {
                receipts = await deliver(work).ConfigureAwait(false);
                if (receipts == null || receipts.Length != work.Entries.Length || receipts.Any(r => r == null))
                    throw new InvalidOperationException("未取得完整的附件回执。");
            }
            catch (Exception error)
            {
                receipts = work.Entries.Select(row => new FileReceipt { Error = error.GetBaseException().Message }).ToArray();
            }
            if (!Stopped) owner.Post(delegate { Complete(work, receipts); }, null);
        }

        internal void Confirm(UploadWork work, int[] indexes)
        {
            if (Stopped || Active != work || indexes.Length == 0) return;
            foreach (int i in indexes) work.Entries[i].Set(UploadState.Added, "");
            Checkpoint();
            Notify();
        }

        private void Complete(UploadWork work, FileReceipt[] receipts)
        {
            if (Stopped) return;
            for (int i = 0; i < work.Entries.Length; i++)
            {
                var row = work.Entries[i];
                var receipt = receipts[i];
                row.Submitted |= receipt.Submitted;
                if (row.State != UploadState.Added)
                    row.Set(receipt.Added ? UploadState.Added : UploadState.Failed, receipt.Error);
            }
            if (Finished != null) Finished(work);
            Publish();
            Active = null;
            if (pending.Count == 0) Notify();
            else StartNext();
        }

        internal void ClearFinished()
        {
            foreach (var row in Entries.Where(r => r.State == UploadState.Added || r.CanRetry).ToArray()) Entries.Remove(row);
            Publish();
        }

        private void Notify() { if (!Stopped && Changed != null) Changed(); }

        // Persist only unresolved local references. Following an interruption a
        // retry first inspects the original composer for a late attachment.
        private sealed class Saved
        {
            public string Batch, Path, Error;
            public int Client;
            public bool NewChat, NewOpened, FromCompare, Submitted;
            public DraftAddress Address;
        }

        internal void Checkpoint()
        {
            try
            {
                var rows = Entries.Where(r => r.State != UploadState.Added).Select(r => new Saved {
                    Batch = r.Destination.Id, Path = r.Path, Client = r.Destination.Client,
                    NewChat = r.Destination.NewChat, NewOpened = r.Destination.NewOpened, FromCompare = r.Destination.FromCompare,
                    Address = r.Destination.Address, Submitted = r.Submitted, Error = r.CanRetry ? r.Detail : null
                }).ToArray();
                if (rows.Length > 0 || File.Exists(journal)) RelayStore.AtomicText(journal, RelayStore.Json.Serialize(rows));
            }
            catch (Exception e) { DropTrace.Write("queue", "save: " + e.GetBaseException().Message); }
        }

        private void Publish()
        {
            foreach (var row in Entries.Where(r => r.State == UploadState.Added).Skip(80).ToArray()) Entries.Remove(row);
            Checkpoint();
            Notify();
        }

        private void Restore()
        {
            if (!File.Exists(journal)) return;
            try
            {
                var groups = new Dictionary<string, UploadDestination>();
                foreach (var saved in RelayStore.Json.Deserialize<Saved[]>(File.ReadAllText(journal)) ?? new Saved[0])
                {
                    if (saved == null || saved.Batch == null || string.IsNullOrWhiteSpace(saved.Path) || (saved.Client != 0 && saved.Client != 1)) continue;
                    UploadDestination dest;
                    if (!groups.TryGetValue(saved.Batch, out dest))
                    {
                        dest = new UploadDestination(saved.Client, saved.NewChat, saved.FromCompare, saved.Batch) {
                            Address = saved.Address, NewOpened = saved.NewOpened
                        };
                        groups.Add(saved.Batch, dest);
                    }
                    var row = new UploadEntry(dest, saved.Path) { Submitted = saved.Submitted };
                    row.Set(UploadState.Failed, saved.Error ?? "侧栏已重启，点重试可继续。");
                    Entries.Add(row);
                }
            }
            catch (Exception e) { DropTrace.Write("queue", "restore: " + e.GetBaseException().Message); }
        }
    }
}
