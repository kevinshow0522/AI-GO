using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

		internal static PlanReport OpenClaudeUsage(IntPtr window)
		{
			if (window == IntPtr.Zero || !Native.IsWindow(window))
			{
				return null;
			}
			foreach (AutomationElement item in ChatUi.Of(AutomationElement.FromHandle(window), ControlType.Document).Reverse())
			{
				Uri uri = ClaudePage(item);
				if (uri != null && uri.AbsolutePath.TrimEnd('/') == "/settings/usage")
				{
					return ReadClaudePage(item);
				}
			}
			return null;
		}

		private static PlanReport ReadClaudePage(AutomationElement doc)
		{
			List<string> list = new List<string>();
			foreach (AutomationElement item in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
			{
				ControlType controlType = item.Current.ControlType;
				string text = ChatUi.Name(item);
				if (controlType == ControlType.ProgressBar)
				{
					RangeValuePattern rangeValuePattern = ChatUi.Pattern<RangeValuePattern>(item, RangeValuePattern.Pattern);
					if (rangeValuePattern != null && rangeValuePattern.Current.Maximum == 100.0)
					{
						text = rangeValuePattern.Current.Value.ToString(CultureInfo.InvariantCulture) + "% used";
					}
				}
				if (text.Length > 0 && text.Length < 160 && (controlType == ControlType.Text || controlType == ControlType.ProgressBar || controlType == ControlType.Header || controlType == ControlType.Group) && (list.Count == 0 || list[list.Count - 1] != text))
				{
					list.Add(text);
				}
			}
			PlanReport planReport = ParseClaude(list);
			if (planReport.Rows.Count <= 0)
			{
				return null;
			}
			return Save("Claude", planReport);
		}

		internal static PlanReport Claude(IntPtr window, Func<IntPtr> discover)
		{
			string text = null;
			if (window != IntPtr.Zero && Native.IsWindow(window))
			{
				ChatUi chatUi = new ChatUi(window, () => false);
				text = chatUi.PageKey();
				if (chatUi.HasDraftOrAttachment())
				{
					throw new IOException("Claude 有未发送的草稿或附件。处理后可刷新额度。");
				}
			}
			bool flag = text == null || !text.EndsWith("/settings/usage", StringComparison.OrdinalIgnoreCase);
			if (flag)
			{
				ProcessStartInfo processStartInfo = new ProcessStartInfo("claude://claude.ai/settings/usage");
				processStartInfo.UseShellExecute = true;
				Process.Start(processStartInfo);
			}
			Uri uri = null;
			bool flag2 = false;
			bool flag3 = false;
			try
			{
				Stopwatch stopwatch = Stopwatch.StartNew();
				while (stopwatch.ElapsedMilliseconds < 10500)
				{
					Thread.Sleep(350);
					if (window == IntPtr.Zero || !Native.IsWindow(window))
					{
						window = discover();
					}
					if (window == IntPtr.Zero)
					{
						continue;
					}
					try
					{
						AutomationElement root = AutomationElement.FromHandle(window);
						foreach (AutomationElement item in ChatUi.Of(root, ControlType.Document).Reverse())
						{
							Uri uri2 = ClaudePage(item);
							if (uri2 == null)
							{
								continue;
							}
							uri = uri2;
							if (Regex.IsMatch(uri2.AbsolutePath, "^/(?:upgrade|plans|pricing)(?:/|$)"))
							{
								flag2 = true;
							}
							else if (!(uri2.AbsolutePath.TrimEnd('/') != "/settings/usage"))
							{
								flag3 = true;
								PlanReport planReport = ReadClaudePage(item);
								if (planReport != null)
								{
									return planReport;
								}
							}
						}
						if (flag2 && stopwatch.ElapsedMilliseconds > 2200)
						{
							break;
						}
					}
					catch (ElementNotAvailableException)
					{
					}
				}
				PlanReport planReport2 = new PlanReport();
				planReport2.Source = "Claude · 官方用量页";
				planReport2.Note = (flag2 ? "官方用量页跳转到了升级页，暂未提供额度和重置时间。" : (flag3 ? "官方用量页未提供可读取的余额或重置时间。" : "未读取到官方用量页。请确认 Claude 登录状态后刷新。"));
				PlanReport r = planReport2;
				return Save("Claude", r);
			}
			finally
			{
				Uri result;
				if (flag && uri != null && Uri.TryCreate(text, UriKind.Absolute, out result) && OfficialClaudeHosts.Contains(result.Host, StringComparer.OrdinalIgnoreCase) && Regex.IsMatch(result.AbsolutePath, "^/(?:new/?$|chat/[a-fA-F0-9-]+/?$)") && window != IntPtr.Zero && Native.IsWindow(window))
				{
					try
					{
						string text2 = new ChatUi(window, () => false).PageKey();
						if (text2 == uri.GetLeftPart(UriPartial.Path) && Regex.IsMatch(uri.AbsolutePath, "^/(settings/usage|upgrade|plans|pricing)(?:/|$)"))
						{
							ProcessStartInfo processStartInfo2 = new ProcessStartInfo("claude://claude.ai" + result.AbsolutePath);
							processStartInfo2.UseShellExecute = true;
							Process.Start(processStartInfo2);
						}
					}
					catch (Exception)
					{
					}
				}
			}
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

		internal static string Describe(PlanReport report)
		{
			if (report == null)
			{
				return "尚未读取。";
			}
			if (report.Rows == null || report.Rows.Count == 0)
			{
				return report.Note ?? "官方尚未提供数据。";
			}
			return string.Join("\r\n", report.Rows.Select(delegate(Allowance w)
			{
				string text = w.Reset;
				if (w.ResetEpoch > 0)
				{
					DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(w.ResetEpoch).ToLocalTime();
					if (dateTimeOffset <= DateTimeOffset.Now)
					{
						return w.Window + "  ·  已到重置时间，请刷新";
					}
					text = dateTimeOffset.ToString("MM-dd HH:mm") + " 重置";
				}
				return w.Window + "  ·  剩余 " + w.Remaining.ToString("0.#") + "%  ·  " + (string.IsNullOrEmpty(text) ? "官方未提供重置时间" : text);
			}));
		}
	}
}
