using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal sealed class UsageSlot
	{
		private Task pending;

		private bool observing;

		internal PlanReport Report { get; private set; }

		internal string Error { get; private set; }

		internal bool Reading { get; private set; }

		internal DateTime LastAttempt { get; private set; }

		internal event Action Changed;

		internal UsageSlot(string app)
		{
			Report = PlanUsage.Cached(app);
		}

		internal async void ObserveOpenClaude(IntPtr window)
		{
			if (observing || Reading || window == IntPtr.Zero)
			{
				return;
			}
			observing = true;
			try
			{
				int num = default(int);
				int num2 = num;
				int num3 = 0;
				try
				{
					PlanReport report = await Task.Run(() => PlanUsage.OpenClaudeUsage(window));
					if (report != null && !Reading && Report.ReadAt <= report.ReadAt)
					{
						Report = report;
						Error = null;
						Notify();
					}
				}
				catch (ElementNotAvailableException)
				{
				}
				catch (COMException)
				{
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
				catch (InvalidOperationException)
				{
				}
			}
			finally
			{
				observing = false;
			}
		}

		internal Task Refresh(Func<PlanReport> read)
		{
			if (pending == null || pending.IsCompleted)
			{
				pending = Read(read);
			}
			return pending;
		}

		private async Task Read(Func<PlanReport> read)
		{
			Reading = true;
			LastAttempt = DateTime.UtcNow;
			Error = null;
			Notify();
			try
			{
				int num = default(int);
				int num2 = num;
				int num3 = 0;
				try
				{
					Report = await Task.Run(read);
				}
				catch (Exception ex)
				{
					Error = ex.GetBaseException().Message;
				}
			}
			finally
			{
				Reading = false;
				Notify();
			}
		}

		private void Notify()
		{
			if (this.Changed != null)
			{
				this.Changed();
			}
		}

		internal Allowance RingRow()
		{
			if (Report == null || Report.Rows == null)
			{
				return null;
			}
			IEnumerable<Allowance> source = Report.Rows.Where((Allowance row) => row != null && row.Remaining >= 0.0 && row.Remaining <= 100.0 && !Expired(row));
			IEnumerable<Allowance> source2 = source.Where((Allowance row) => !(row.Window ?? "").Contains(" · ") && !Regex.IsMatch(row.Window ?? "", "(?:only|^仅|^僅)", RegexOptions.IgnoreCase));
			return source2.OrderBy((Allowance row) => row.Remaining).FirstOrDefault() ?? source.OrderBy((Allowance row) => row.Remaining).FirstOrDefault();
		}

		internal bool Expired(Allowance row)
		{
			long num = DateTime.UtcNow.Ticks - Report.ReadAt;
			if (Report.ReadAt > 0 && num >= 0 && num <= TimeSpan.FromMinutes(15.0).Ticks)
			{
				if (row.ResetEpoch > 0)
				{
					return row.ResetEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
				}
				return false;
			}
			return true;
		}

		internal static bool Low(double? remaining, int threshold)
		{
			if (threshold > 0 && remaining.HasValue && remaining.Value >= 0.0)
			{
				return remaining.Value < (double)threshold;
			}
			return false;
		}

		internal string Tip(string title)
		{
			Allowance allowance = RingRow();
			if (allowance == null)
			{
				return title + " · " + (Reading ? "正在读取额度" : ((Report.Rows != null && Report.Rows.Count > 0) ? "额度待刷新" : "额度暂不可用")) + "，点击查看";
			}
			return title + " · " + allowance.Window + "剩余 " + allowance.Remaining.ToString("0.#") + "%\n" + ResetText(allowance);
		}

		internal static string ResetText(Allowance row)
		{
			if (row.ResetEpoch > 0 && row.ResetEpoch <= 253402300799L)
			{
				return DateTimeOffset.FromUnixTimeSeconds(row.ResetEpoch).ToLocalTime().ToString("MM-dd HH:mm") + " 重置";
			}
			if (!string.IsNullOrWhiteSpace(row.Reset))
			{
				return row.Reset;
			}
			return "官方未提供重置时间";
		}
	}
}
