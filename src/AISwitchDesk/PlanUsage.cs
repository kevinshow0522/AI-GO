using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal static class PlanUsage
	{
		private static readonly string[] OfficialClaudeHosts = new string[2] { "claude.ai", "www.claude.ai" };

		private static string Cache(string app)
		{
			return Path.Combine(RelayStore.Root, "quota-" + app + ".json");
		}

		internal static PlanReport Cached(string app)
		{
			try
			{
				if (File.Exists(Cache(app)))
				{
					PlanReport planReport = RelayStore.Json.Deserialize<PlanReport>(File.ReadAllText(Cache(app)));
					if (planReport != null)
					{
						return planReport;
					}
				}
			}
			catch (Exception)
			{
			}
			PlanReport planReport2 = new PlanReport();
			planReport2.Source = ((app == "ChatGPT") ? "OpenAI · Codex" : "Claude");
			planReport2.Note = "尚未读取官方套餐数据。";
			return planReport2;
		}

		private static PlanReport Save(string app, PlanReport r)
		{
			r.ReadAt = DateTime.UtcNow.Ticks;
			RelayStore.AtomicText(Cache(app), RelayStore.Json.Serialize(r));
			return r;
		}

		private static Dictionary<string, object> Map(object o)
		{
			return o as Dictionary<string, object>;
		}

		private static object Get(Dictionary<string, object> d, string key)
		{
			object value;
			if (d == null || !d.TryGetValue(key, out value))
			{
				return null;
			}
			return value;
		}

		private static Dictionary<string, object> Response(Process p, int id)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < 12000)
			{
				Task<string> task = p.StandardOutput.ReadLineAsync();
				if (!task.Wait(Math.Max(1, 12000 - (int)stopwatch.ElapsedMilliseconds)) || task.Result == null)
				{
					break;
				}
				Dictionary<string, object> d = RelayStore.Json.Deserialize<Dictionary<string, object>>(task.Result);
				if (!(Convert.ToString(Get(d, "id")) != id.ToString(CultureInfo.InvariantCulture)))
				{
					Dictionary<string, object> dictionary = Map(Get(d, "result"));
					if (dictionary == null)
					{
						throw new IOException("官方客户端未返回额度，请确认登录状态。");
					}
					return dictionary;
				}
			}
			throw new IOException("官方额度读取超时。");
		}

		internal static PlanReport OpenAI()
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
			string text = (Directory.Exists(path) ? (from x in Directory.GetDirectories(path)
				select Path.Combine(x, "codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null);
			if (text == null)
			{
				throw new IOException("未找到本机 OpenAI 客户端的额度接口。");
			}
			ProcessStartInfo processStartInfo = new ProcessStartInfo(text, "app-server --stdio");
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.RedirectStandardInput = true;
			processStartInfo.RedirectStandardOutput = true;
			processStartInfo.RedirectStandardError = true;
			processStartInfo.WorkingDirectory = RelayStore.Root;
			ProcessStartInfo startInfo = processStartInfo;
			Directory.CreateDirectory(RelayStore.Root);
			using (Process process = Process.Start(startInfo))
			{
				process.ErrorDataReceived += delegate
				{
				};
				process.BeginErrorReadLine();
				try
				{
					process.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai_switch_desk\",\"title\":\"AI Switch Desk\",\"version\":\"2.0\"}}}");
					process.StandardInput.Flush();
					Response(process, 1);
					process.StandardInput.WriteLine("{\"method\":\"initialized\"}");
					process.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
					process.StandardInput.Flush();
					Dictionary<string, object> d = Response(process, 2);
					Dictionary<string, object> dictionary = Map(Get(d, "rateLimitsByLimitId"));
					PlanReport planReport = new PlanReport();
					planReport.Source = "OpenAI · Codex";
					PlanReport planReport2 = planReport;
					if (dictionary == null || dictionary.Count == 0)
					{
						AddWindows(planReport2, Map(Get(d, "rateLimits")), "");
					}
					else
					{
						foreach (KeyValuePair<string, object> item in dictionary)
						{
							AddWindows(planReport2, Map(item.Value), (item.Key == "codex") ? "" : (item.Key + " · "));
						}
					}
					if (planReport2.Rows.Count == 0)
					{
						planReport2.Note = "账户当前没有公布可读的套餐额度。";
					}
					return Save("ChatGPT", planReport2);
				}
				finally
				{
					try
					{
						process.StandardInput.Close();
						if (!process.WaitForExit(1200))
						{
							process.Kill();
						}
					}
					catch (InvalidOperationException)
					{
					}
				}
			}
		}

		private static void AddWindows(PlanReport report, Dictionary<string, object> bucket, string prefix)
		{
			string[] array = new string[2] { "primary", "secondary" };
			foreach (string key in array)
			{
				Dictionary<string, object> dictionary = Map(Get(bucket, key));
				if (dictionary != null && Get(dictionary, "usedPercent") != null)
				{
					int num = Convert.ToInt32(Get(dictionary, "windowDurationMins"));
					double num2 = Convert.ToDouble(Get(dictionary, "usedPercent"), CultureInfo.InvariantCulture);
					if (!(num2 < 0.0) && !(num2 > 100.0))
					{
						string text = ((num <= 0) ? "套餐" : ((num % 1440 == 0) ? (num / 1440 + " 天") : ((num % 60 == 0) ? (num / 60 + " 小时") : (num + " 分钟"))));
						report.Rows.Add(new Allowance
						{
							Window = prefix + text,
							Remaining = 100.0 - num2,
							ResetEpoch = Convert.ToInt64(Get(dictionary, "resetsAt"))
						});
					}
				}
			}
		}

		private static Uri ClaudePage(AutomationElement d)
		{
			Uri uri = ChatUi.Page(d);
			if (!(uri != null) || !(uri.Scheme == "https") || !OfficialClaudeHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
			{
				return null;
			}
			return uri;
		}

		private const string ClaudeUsageLink = "claude://claude.ai/settings/usage";

		// Claude words the same allowance differently in different places: the
		// settings page says "当前会话", the usage panel in Code says "5 小时限额".
		private const string ClaudeSession = "^(current session|5[\\s\\-]*hours?\\b.*|[5五]\\s*小[时時].*|当前(会话|时段|工作阶段)|本次(会话|工作阶段)|目前的?工作階段|當前工作階段)$";

		// Words that say "the week's allowance" without naming a model.
		private const string ClaudeWeekWords = "this week|weekly|all models|limits?|usage|本[周週]|每[周週]|周限额|限额|限額|限制|用量|额度|額度|所有模型|全部模型";

		// The name an allowance is kept under: "当前会话", "每周", or "仅 " and a
		// model's name for that model's share. "每周 Fable 限额" is such a share,
		// not the week's allowance.
		internal static string ClaudeWindow(string label)
		{
			label = (label ?? "").Trim();
			if (Regex.IsMatch(label, ClaudeSession, RegexOptions.IgnoreCase))
			{
				return "当前会话";
			}
			if (Regex.IsMatch(label, "(?:only|^仅|^僅)", RegexOptions.IgnoreCase))
			{
				return label;
			}
			string model = Regex.Replace(label, ClaudeWeekWords, " ", RegexOptions.IgnoreCase);
			model = Regex.Replace(model, "[\\s·・:：()（）\\-–—|/]+", " ").Trim();
			return (model.Length == 0) ? "每周" : ("仅 " + model);
		}

		// Headings that follow the plan's own allowances: reset coupons, bought
		// credits, and how the week's use splits between products.
		private const string ClaudeOtherSections = "^(限额重置|limit resets?|用量额度|usage credits|extra usage|额外用量|額外用量|使用额度|用量点数|用量點數|.*各产品.*|.*各產品.*|.*by product.*)$";

		// A bar for the session or for a week, however it is worded.
		private static bool IsAllowanceBar(AutomationElement bar)
		{
			string name = ChatUi.Name(bar);
			return Regex.IsMatch(name, ClaudeSession, RegexOptions.IgnoreCase) || Regex.IsMatch(name, "week|[周週]", RegexOptions.IgnoreCase);
		}

		// The claude.ai page holding the usage figures, when Claude is showing them.
		private static AutomationElement ClaudeUsagePage(IntPtr window)
		{
			if (window == IntPtr.Zero || !Native.IsWindow(window))
			{
				return null;
			}
			try
			{
				AutomationElement[] pages = ChatUi.Of(AutomationElement.FromHandle(window), ControlType.Document).Reverse().Where((AutomationElement item) => ClaudePage(item) != null).ToArray();
				foreach (AutomationElement item in pages)
				{
					if (ClaudePage(item).AbsolutePath.TrimEnd('/') == "/settings/usage")
					{
						return item;
					}
				}
				// Settings opens as a layer over the page Claude was on, and the
				// address can stay that page's. The allowance bars then tell.
				foreach (AutomationElement item in pages)
				{
					if (ChatUi.Of(item, ControlType.ProgressBar).Any(IsAllowanceBar))
					{
						return item;
					}
				}
			}
			catch (ElementNotAvailableException)
			{
			}
			return null;
		}

		// Reads the usage page if it is open. Never opens it.
		internal static PlanReport OpenClaudeUsage(IntPtr window)
		{
			AutomationElement page = ClaudeUsagePage(window);
			PlanReport report = (page == null) ? null : ReadClaudePage(page);
			return (report == null) ? null : Save("Claude", report);
		}

		// For when the person asks for a fresh reading: opens the usage page,
		// waits for its figures, and closes it again so Claude is back where it was.
		internal static PlanReport FetchClaudeUsage(IntPtr window)
		{
			if (window == IntPtr.Zero || !Native.IsWindow(window))
			{
				throw new IOException("未找到 Claude 窗口。");
			}
			bool alreadyOpen = ClaudeUsagePage(window) != null;
			if (!alreadyOpen)
			{
				ProcessStartInfo link = new ProcessStartInfo(ClaudeUsageLink);
				link.UseShellExecute = true;
				using (Process.Start(link))
				{
				}
			}
			PlanReport latest = null;
			string seen = null;
			AutomationElement page = null;
			Stopwatch clock = Stopwatch.StartNew();
			// The page usually fills in within three seconds, but can sit on "加载中" much longer.
			while (clock.ElapsedMilliseconds < 20000)
			{
				Thread.Sleep(400);
				try
				{
					page = ClaudeUsagePage(window);
					PlanReport report = (page == null) ? null : ReadClaudePage(page);
					if (report == null)
					{
						continue;
					}
					string figures = string.Join("|", report.Rows.Select((Allowance row) => row.Window + "=" + row.Remaining.ToString(CultureInfo.InvariantCulture) + "@" + row.Reset));
					latest = report;
					// The same figures twice in a row: the page has finished loading.
					if (figures == seen)
					{
						break;
					}
					seen = figures;
				}
				catch (ElementNotAvailableException)
				{
				}
				catch (COMException)
				{
				}
			}
			if (!alreadyOpen && page != null)
			{
				CloseClaudeSettings(page);
			}
			DropTrace.Write("usage", (latest == null) ? "claude page gave no figures" : ("claude page rows=" + latest.Rows.Count));
			if (latest == null)
			{
				throw new IOException("没能从 Claude 的用量页读到额度，可在 Claude「设置 → 使用情况」里查看。");
			}
			return Save("Claude", latest);
		}

		// Settings is a layer over the page Claude was on; its close button returns
		// there. It is the last "close" before the allowances, so a banner's own
		// close button on the page underneath is not taken for it.
		private static void CloseClaudeSettings(AutomationElement page)
		{
			try
			{
				AutomationElement close = null;
				foreach (AutomationElement item in page.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
				{
					ControlType kind = item.Current.ControlType;
					if (kind == ControlType.Button && Regex.IsMatch(ChatUi.Name(item), "^(关闭|關閉|close)$", RegexOptions.IgnoreCase))
					{
						close = item;
					}
					else if (kind == ControlType.ProgressBar && IsAllowanceBar(item))
					{
						break;
					}
				}
				InvokePattern press = (close == null) ? null : ChatUi.Pattern<InvokePattern>(close, InvokePattern.Pattern);
				if (press != null)
				{
					press.Invoke();
				}
			}
			catch (ElementNotAvailableException)
			{
			}
			catch (COMException)
			{
			}
			catch (InvalidOperationException)
			{
			}
		}

		private static PlanReport ReadClaudePage(AutomationElement doc)
		{
			List<string> text = new List<string>();
			List<double> used = new List<double>();
			List<string> lines = new List<string>();
			foreach (AutomationElement item in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
			{
				ControlType kind;
				string name;
				double bar = -1.0;
				try
				{
					kind = item.Current.ControlType;
					name = ChatUi.Name(item);
					if (kind == ControlType.ProgressBar)
					{
						RangeValuePattern range = ChatUi.Pattern<RangeValuePattern>(item, RangeValuePattern.Pattern);
						if (range != null && range.Current.Maximum == 100.0)
						{
							bar = range.Current.Value;
						}
					}
				}
				catch (ElementNotAvailableException)
				{
					// The page redraws while it is read. Skip what just went away.
					continue;
				}
				if (kind != ControlType.Text && kind != ControlType.ProgressBar && kind != ControlType.Header && kind != ControlType.Group)
				{
					continue;
				}
				if (bar >= 0.0)
				{
					text.Add(name);
					used.Add(bar);
				}
				else if (name.Length > 0 && name.Length < 160 && kind != ControlType.ProgressBar)
				{
					text.Add(name);
					used.Add(-1.0);
				}
				string line = (bar >= 0.0) ? (bar.ToString(CultureInfo.InvariantCulture) + "% used") : name;
				if (line.Length > 0 && line.Length < 160 && (lines.Count == 0 || lines[lines.Count - 1] != line))
				{
					lines.Add(line);
				}
			}
			PlanReport planReport = ParseClaudeBars(text.ToArray(), used.ToArray(), DateTime.Now);
			if (planReport.Rows.Count == 0)
			{
				// An earlier layout of the page: headings and plain text.
				planReport = ParseClaude(lines);
			}
			if (planReport.Rows.Count <= 0)
			{
				return null;
			}
			return planReport;
		}

		// The usage page as a flat list in reading order. Each allowance is a
		// progress bar named after it, holding the percentage used, with its reset
		// time in the text just before. used[i] is that percentage, or below 0
		// where text[i] is plain text.
		internal static PlanReport ParseClaudeBars(string[] text, double[] used, DateTime now)
		{
			PlanReport report = new PlanReport();
			report.Source = "Claude · 官方用量页";
			string reset = null;
			for (int i = 0; i < text.Length && i < used.Length; i++)
			{
				string line = (text[i] ?? "").Trim();
				if (used[i] < 0.0)
				{
					if (Regex.IsMatch(line, ClaudeOtherSections, RegexOptions.IgnoreCase))
					{
						break;
					}
					// A reset line is short and names a time.
					if (line.Length <= 40 && Regex.IsMatch(line, "reset|重置|重設", RegexOptions.IgnoreCase) && Regex.IsMatch(line, "\\d"))
					{
						reset = line;
					}
					continue;
				}
				if (used[i] > 100.0 || line.Length == 0)
				{
					continue;
				}
				string window = ClaudeWindow(line);
				// A bar for one model, or for anything else, counts only with a
				// reset time of its own, which the per-product split's bars lack.
				if (reset == null && window != "当前会话" && window != "每周")
				{
					continue;
				}
				// The same allowance shown twice is kept once.
				if (report.Rows.Any((Allowance row) => row.Window == window))
				{
					reset = null;
					continue;
				}
				string after = (i + 1 < text.Length && used[i + 1] < 0.0) ? (text[i + 1] ?? "") : "";
				bool left = Regex.IsMatch(after, "[%％]") && Regex.IsMatch(after, "remaining|left|剩余|剩餘|可用", RegexOptions.IgnoreCase);
				Allowance allowance = new Allowance();
				allowance.Window = window;
				allowance.Remaining = left ? used[i] : (100.0 - used[i]);
				allowance.Reset = reset;
				allowance.ResetEpoch = ResetEpoch(reset, now);
				report.Rows.Add(allowance);
				reset = null;
			}
			return report;
		}

		// When a line such as "5:00 重置", "星期六21:00 重置" or "Resets in 3 hr 12 min"
		// falls, as Unix seconds. 0 when the line does not say clearly.
		internal static long ResetEpoch(string reset, DateTime now)
		{
			if (string.IsNullOrEmpty(reset))
			{
				return 0L;
			}
			DateTime at;
			Match clock = Regex.Match(reset, "(\\d{1,2})[:：](\\d{2})");
			if (clock.Success)
			{
				int hour = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
				int minute = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
				if (hour < 12 && Regex.IsMatch(reset, "下午|晚上|pm\\b|p\\.m\\.", RegexOptions.IgnoreCase))
				{
					hour += 12;
				}
				else if (hour == 12 && Regex.IsMatch(reset, "上午|凌晨|am\\b|a\\.m\\.", RegexOptions.IgnoreCase))
				{
					hour = 0;
				}
				if (hour > 23 || minute > 59)
				{
					return 0L;
				}
				TimeSpan time = new TimeSpan(hour, minute, 0);
				Match date = Regex.Match(reset, "(\\d{1,2})\\s*月\\s*(\\d{1,2})\\s*日");
				Match weekday = Regex.Match(reset, "(?:星期|礼拜|禮拜|周|週)([一二三四五六日天])|\\b(sun|mon|tue|wed|thu|fri|sat)", RegexOptions.IgnoreCase);
				if (date.Success)
				{
					int month = int.Parse(date.Groups[1].Value, CultureInfo.InvariantCulture);
					int day = int.Parse(date.Groups[2].Value, CultureInfo.InvariantCulture);
					if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(now.Year, month))
					{
						return 0L;
					}
					at = new DateTime(now.Year, month, day) + time;
					if (at <= now)
					{
						at = at.AddYears(1);
					}
				}
				else if (Regex.IsMatch(reset, "(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\\.?\\s+\\d{1,2}|\\d{1,2}/\\d{1,2}", RegexOptions.IgnoreCase))
				{
					// A calendar date in a form this does not read.
					return 0L;
				}
				else if (weekday.Success)
				{
					int target = weekday.Groups[1].Success
						? "日一二三四五六".IndexOf(weekday.Groups[1].Value.Replace('天', '日'), StringComparison.Ordinal)
						: Array.IndexOf(new string[7] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }, weekday.Groups[2].Value.ToLowerInvariant());
					at = now.Date.AddDays((target - (int)now.DayOfWeek + 7) % 7) + time;
					if (at <= now)
					{
						at = at.AddDays(7.0);
					}
				}
				else
				{
					at = now.Date + time;
					if (at <= now)
					{
						at = at.AddDays(1.0);
					}
				}
			}
			else
			{
				Match days = Regex.Match(reset, "(\\d+)\\s*(?:天|days?)", RegexOptions.IgnoreCase);
				Match hours = Regex.Match(reset, "(\\d+)\\s*(?:小时|小時|hours?|hrs?)", RegexOptions.IgnoreCase);
				Match minutes = Regex.Match(reset, "(\\d+)\\s*(?:分钟|分鐘|minutes?|mins?)", RegexOptions.IgnoreCase);
				if (!days.Success && !hours.Success && !minutes.Success)
				{
					return 0L;
				}
				at = now;
				if (days.Success) at = at.AddDays(Math.Min(400, int.Parse(days.Groups[1].Value, CultureInfo.InvariantCulture)));
				if (hours.Success) at = at.AddHours(Math.Min(9600, int.Parse(hours.Groups[1].Value, CultureInfo.InvariantCulture)));
				if (minutes.Success) at = at.AddMinutes(Math.Min(576000, int.Parse(minutes.Groups[1].Value, CultureInfo.InvariantCulture)));
			}
			return new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Local)).ToUnixTimeSeconds();
		}

		internal static PlanReport ParseClaude(IEnumerable<string> lines)
		{
			PlanReport planReport = new PlanReport();
			planReport.Source = "Claude · 官方用量页";
			PlanReport planReport2 = planReport;
			string text = null;
			string reset = null;
			Allowance allowance = null;
			foreach (string line in lines)
			{
				string text2 = null;
				if (Regex.IsMatch(line, "^(current session|当前(会话|时段|工作阶段)|本次(会话|工作阶段)|目前的?工作階段|當前工作階段)$", RegexOptions.IgnoreCase))
				{
					text2 = "当前会话";
				}
				else if (Regex.IsMatch(line, "^(weekly limits?|每[周週](限额|限制|用量)|周限额|週使用量限制)$", RegexOptions.IgnoreCase))
				{
					text2 = "每周";
				}
				else if (Regex.IsMatch(line, "^(all models|所有模型|全部模型|sonnet only|opus only|fable only|仅 .+|僅 .+)$", RegexOptions.IgnoreCase))
				{
					text2 = line;
				}
				if (text2 != null)
				{
					text = text2;
					reset = null;
					allowance = null;
					continue;
				}
				if (Regex.IsMatch(line, "^(extra usage|usage credits|额外用量|額外用量|使用额度|用量点数|用量點數)", RegexOptions.IgnoreCase))
				{
					text = null;
					allowance = null;
				}
				if (text == null)
				{
					continue;
				}
				if (Regex.IsMatch(line, "reset|重置|重設|重設於", RegexOptions.IgnoreCase))
				{
					reset = line;
					if (allowance != null)
					{
						allowance.Reset = line;
					}
				}
				Match match = Regex.Match(line, "(\\d{1,3}(?:[.,]\\d+)?)\\s*[%％]");
				double result;
				if (match.Success && allowance == null && !Regex.IsMatch(line, "节省|折扣|save|discount|節省", RegexOptions.IgnoreCase) && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !(result > 100.0))
				{
					bool flag = Regex.IsMatch(line, "remaining|left|剩余|剩餘|可用", RegexOptions.IgnoreCase);
					Allowance allowance2 = new Allowance();
					allowance2.Window = text;
					allowance2.Remaining = (flag ? result : (100.0 - result));
					allowance2.Reset = reset;
					allowance = allowance2;
					planReport2.Rows.Add(allowance);
				}
			}
			return planReport2;
		}
	}
}
