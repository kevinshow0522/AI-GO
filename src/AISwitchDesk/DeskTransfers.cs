using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AISwitchDesk
{
    internal enum TransferMark { None, Waiting, Working, Success, Attention }

    internal sealed partial class DeskForm
    {
        private const double SuccessSeconds = 2.4;
        private readonly Timer transferMotion = new Timer { Interval = 35 };
        private readonly Stopwatch transferClock = Stopwatch.StartNew();
        private readonly double[] successUntil = new double[2];
        private readonly int[] waiting = new int[2], issues = new int[2];
        private readonly HashSet<string> seenReceipts = new HashSet<string>();
        private readonly long openedAt = DateTime.UtcNow.Ticks;
        private int relayTarget = -1, trailFrom = -1, trailTo = -1;
        private float travelPhase;

        private void InitializeTransferFeedback()
        {
            Uploads.Changed += QueueFeedback;
            Uploads.Finished += delegate(UploadWork work) {
                int count = work.Entries.Count(row => row.State == UploadState.Added);
                if (count == work.Entries.Length)
                    successUntil[work.Destination.Client] = transferClock.Elapsed.TotalSeconds + SuccessSeconds;
                if (!SettingsOpen)
                    Notice(work.Destination.Client, work.Destination.App + " 已加入 " + count + "/" + work.Entries.Length +
                        (count == work.Entries.Length ? " 个附件" : " · 右键并排图标可逐项重试"));
            };
            transferMotion.Tick += delegate { TransferFrame(); };
            QueueFeedback();
        }

        private void QueueFeedback()
        {
            for (int i = 0; i < clients.Length; i++)
            {
                waiting[i] = Uploads.Entries.Count(r => r.Destination.Client == i && r.State == UploadState.Waiting);
                issues[i] = Uploads.Entries.Count(r => r.Destination.Client == i && r.CanRetry);
                var work = Uploads.Active;
                string hint = clients[i].Name + " · 拖到图标加入当前对话，拖到「新建聊天」另开聊天";
                if (work != null && work.Destination.Client == i)
                    hint = clients[i].Name + " 已加入 " + work.Entries.Count(e => e.State == UploadState.Added) + "/" + work.Entries.Length;
                if (waiting[i] != 0) hint += " · " + waiting[i] + " 项排队中";
                if (issues[i] != 0) hint += " · " + issues[i] + " 项待重试，右键并排图标查看";
                tips.SetToolTip(buttons[i], hint);
            }
            TransferFrame();
        }

        internal void ObserveHandoffs(IEnumerable<Handoff> rows)
        {
            if (closing) return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)delegate { ObserveHandoffs(rows); });
                return;
            }
            int nextTarget = -1;
            foreach (var job in rows)
            {
                if (job.LinkedId != null || (job.To != "ChatGPT" && job.To != "Claude")) continue;
                int to = job.To == "ChatGPT" ? 0 : 1;
                bool outbound = job.Stage == "await-result" || (job.Stage == "done" && !job.RoundTrip);
                if (outbound) ObserveReceipt(job, ":out", to);
                if (job.Stage == "done" && job.RoundTrip) ObserveReceipt(job, ":back", 1 - to);
                if (nextTarget < 0 && !job.Held &&
                    (job.Stage == "ready" || job.Stage == "confirm" || job.Returning))
                    nextTarget = job.Returning ? 1 - to : to;
            }
            relayTarget = nextTarget;
            TransferFrame();
        }

        private void ObserveReceipt(Handoff job, string leg, int client)
        {
            if (seenReceipts.Add(job.Id + leg) && job.Updated >= openedAt)
                successUntil[client] = transferClock.Elapsed.TotalSeconds + SuccessSeconds;
        }

        private void TransferFrame()
        {
            if (closing || buttons[0] == null) return;
            double now = transferClock.Elapsed.TotalSeconds;
            bool animate = RailMotion.Enabled;
            var active = Uploads.Active;
            int adding = active != null && active.Entries.Any(e => e.State == UploadState.Adding) ? active.Destination.Client : -1;
            trailTo = adding >= 0 && active.Destination.FromCompare ? adding : relayTarget;
            trailFrom = trailTo < 0 ? -1 : adding >= 0 && active.Destination.FromCompare ? (int)RailAction.Compare : 1 - relayTarget;
            travelPhase = animate ? (float)(now / 1.05 % 1) : 0.63f;
            bool needsFrames = trailTo >= 0;
            for (int i = 0; i < 2; i++)
            {
                TransferMark mark;
                if (adding == i || relayTarget == i) mark = TransferMark.Working;
                else if (successUntil[i] > now) mark = TransferMark.Success;
                else if (issues[i] > 0) mark = TransferMark.Attention;
                else if (waiting[i] > 0) mark = TransferMark.Waiting;
                else mark = TransferMark.None;
                var button = buttons[i];
                bool changed = button.Feedback != mark;
                button.Feedback = mark;
                button.FeedbackPhase = mark == TransferMark.Success ?
                    (animate ? (float)Math.Min(1, (now + SuccessSeconds - successUntil[i]) / .24) : 1f) : travelPhase;
                if (changed || ((mark == TransferMark.Working || mark == TransferMark.Success) && animate)) button.Invalidate();
                if (changed)
                    button.AccessibleDescription = mark == TransferMark.Attention ? "文件待重试，可在文件队列中查看" :
                        mark == TransferMark.Success ? "文件已送达" : mark == TransferMark.Working ? "正在传递文件" :
                        mark == TransferMark.Waiting ? "文件排队中" : "";
                needsFrames |= mark == TransferMark.Working || mark == TransferMark.Success;
            }
            // This narrow strip is outside the button hit areas.
            Invalidate(new Rectangle(Width - P(7), SurfaceTop + P(21), P(7), P(132)));
            bool expires = successUntil.Any(t => t > now);
            if ((animate && needsFrames && !collapsed) || expires)
            {
                int interval = animate && !collapsed ? 35 : 220;
                if (transferMotion.Interval != interval) transferMotion.Interval = interval;
                if (!transferMotion.Enabled) transferMotion.Start();
            }
            else transferMotion.Stop();
        }

        private void PaintTransferTrail(Graphics g)
        {
            if (trailTo < 0 || expansion < .94f) return;
            float x = Width - P(4.0);
            float from = P(ButtonTop[trailFrom] + 21.5), to = P(ButtonTop[trailTo] + 21.5);
            float direction = Math.Sign(to - from);
            float y = from + (to - from) * travelPhase;
            var color = DeskTheme.Current.AccentLine;
            using (var track = new Pen(Color.FromArgb(85, color), P(1)))
            using (var dash = new Pen(color, P(2)))
            {
                dash.StartCap = dash.EndCap = LineCap.Round;
                g.DrawLine(track, x, from, x, to);
                g.DrawLine(dash, x, y - P(4) * direction, x, y);
                g.DrawLine(dash, x - P(2), y - P(2) * direction, x, y);
                g.DrawLine(dash, x + P(2), y - P(2) * direction, x, y);
            }
        }
    }
}
