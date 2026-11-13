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

		// How long ago the report was read. MaxValue when it never was.
		internal TimeSpan Age
		{
			get
			{
				long num = DateTime.UtcNow.Ticks - Report.ReadAt;
				if (Report.ReadAt <= 0 || num < 0)
				{
					return TimeSpan.MaxValue;
				}
				return TimeSpan.FromTicks(num);
			}
		}

		// An older reading is still shown, with its age. An allowance only goes
		// down until it resets, so an old figure is an upper bound.
		internal bool Stale
		{
			get
			{
				return Age > TimeSpan.FromMinutes(15.0);
			}
		}

		// True once the figure can no longer be right: its reset time has passed.
		// Without an exact reset time, the length of the window is the limit.
		internal bool Expired(Allowance row)
		{
			TimeSpan age = Age;
			if (age == TimeSpan.MaxValue)
			{
				return true;
			}
			if (row.ResetEpoch > 0)
			{
				return row.ResetEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			}
			return age > ((row.Window == "当前会话") ? TimeSpan.FromHours(5.0) : TimeSpan.FromDays(7.0));
		}

		internal string AgeText()
		{
			TimeSpan age = Age;
			if (age.TotalMinutes < 1.0)
			{
				return "刚刚";
			}
			if (age.TotalHours < 1.0)
			{
				return (int)age.TotalMinutes + " 分钟前";
			}
			if (age.TotalDays < 1.0)
			{
				return (int)age.TotalHours + " 小时前";
			}
			return (int)age.TotalDays + " 天前";
		}

		internal string Amount(Allowance row, IFormatProvider culture)
		{
			if (Expired(row))
			{
				return "待刷新";
			}
			return "剩余 " + row.Remaining.ToString("0.#", culture) + "%" + (Stale ? (" · " + AgeText() + "读取") : "");
		}

		// A relative reset such as "3 小时后重置" was true when it was read.
		internal string ResetLine(Allowance row)
		{
			string text = ResetText(row);
			if (Stale && row.ResetEpoch <= 0 && !string.IsNullOrWhiteSpace(row.Reset))
			{
				return text + "（读取时）";
			}
			return text;
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
			return title + " · " + allowance.Window + "剩余 " + allowance.Remaining.ToString("0.#") + "%" + (Stale ? ("（" + AgeText() + "读取）") : "") + "\n" + ResetLine(allowance);
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
