using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal static class BackgroundRelay
	{
		private const string MutexName = "Local\\AISwitchDesk.Background.v1";

		private const string StopName = "Local\\AISwitchDesk.Background.Stop.v1";

		private static string PauseFile
		{
			get
			{
				return Path.Combine(RelayStore.Root, "automatic.paused");
			}
		}

		internal static bool Enabled
		{
			get
			{
				return !File.Exists(PauseFile);
			}
		}

		internal static void SetEnabled(bool enabled)
		{
			if (enabled)
			{
				if (File.Exists(PauseFile))
				{
					File.Delete(PauseFile);
				}
				EnsureRunning();
			}
			else
			{
				RelayStore.AtomicText(PauseFile, "paused");
			}
		}

		internal static void EnsureRunning()
		{
			Mutex result;
			if (Mutex.TryOpenExisting("Local\\AISwitchDesk.Background.v1", out result))
			{
				result.Dispose();
			}
			else
			{
				Native.StartBackground(Application.ExecutablePath);
			}
		}

		internal static void Stop()
		{
			EventWaitHandle result;
			if (EventWaitHandle.TryOpenExisting("Local\\AISwitchDesk.Background.Stop.v1", out result))
			{
				using (result)
				{
					result.Set();
				}
			}
		}

		internal static bool IsClient(IntPtr window, string app)
		{
			uint pid;
			if (Native.IsWindow(window) && Native.GetWindowThreadProcessId(window, out pid) != 0)
			{
				return ClientApps.OwnsProcess(app, pid);
			}
			return false;
		}

		internal static List<IntPtr> Windows(string app)
		{
			List<IntPtr> result = new List<IntPtr>();
			Native.WindowVisitor visitor = delegate(IntPtr h, IntPtr state)
			{
				StringBuilder stringBuilder = new StringBuilder(64);
				Native.GetClassName(h, stringBuilder, stringBuilder.Capacity);
				if (stringBuilder.ToString() != "Chrome_WidgetWin_1" || ((uint)Native.GetBits(h, -20) & 0x80u) != 0)
				{
					return true;
				}
				if (!Native.IsWindowVisible(h) || Native.GetWindow(h, 4u) != IntPtr.Zero)
				{
					return true;
				}
				Native.Rect rect;
				Native.GetWindowRect(h, out rect);
				if (Native.IsIconic(h))
				{
					Native.Placement placement = default(Native.Placement);
					placement.Length = Marshal.SizeOf(typeof(Native.Placement));
					Native.Placement position = placement;
					if (Native.GetWindowPlacement(h, ref position))
					{
						rect = position.NormalPosition;
					}
				}
				if (rect.Width < 320 || rect.Height < 180 || !IsClient(h, app))
				{
					return true;
				}
				result.Add(h);
				return true;
			};
			Native.EnumWindows(visitor, IntPtr.Zero);
			return result;
		}

		internal static void Hold(Handoff j, string reason)
		{
			try
			{
				Handoff handoff = RelayStore.Load(j.Id);
				if (!(handoff.Stage == "done") && !(handoff.Stage == "cancel") && handoff.Revision == j.Revision)
				{
					handoff.Held = true;
					handoff.Detail = reason;
					RelayStore.Save(handoff);
				}
			}
			catch (IOException)
			{
			}
			catch (InvalidOperationException)
			{
			}
		}

		private static readonly object reportSync = new object();

		private static readonly Dictionary<string, KeyValuePair<string, DateTime>> reported = new Dictionary<string, KeyValuePair<string, DateTime>>();

		// Appends the whole failure to background.log. A loop that fails the same
		// way every second writes once a minute. Logging itself never throws.
		internal static void Report(string operation, Exception ex)
		{
			try
			{
				string text = ex.ToString();
				lock (reportSync)
				{
					KeyValuePair<string, DateTime> last;
					if (reported.TryGetValue(operation, out last) && last.Key == text && last.Value > DateTime.UtcNow)
					{
						return;
					}
					reported[operation] = new KeyValuePair<string, DateTime>(text, DateTime.UtcNow.AddMinutes(1.0));
					Directory.CreateDirectory(RelayStore.Root);
					string path = Path.Combine(RelayStore.Root, "background.log");
					if (File.Exists(path) && new FileInfo(path).Length > 512000)
					{
						File.Delete(path + ".old");
						File.Move(path, path + ".old");
					}
					File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + operation + "] " + text + Environment.NewLine, RelayStore.Utf8);
				}
			}
			catch (Exception)
			{
			}
		}

		// Keeps one background loop alive. A failure that escapes the loop is
		// logged and the loop starts again, instead of leaving a running process
		// that no longer delivers anything.
		private static void Guard(string operation, WaitHandle stop, Action loop)
		{
			while (!stop.WaitOne(0))
			{
				try
				{
					loop();
				}
				catch (Exception ex)
				{
					if (stop.WaitOne(0))
					{
						return;
					}
					Report(operation + " loop restarted", ex);
				}
				stop.WaitOne(3000);
			}
		}

		internal static void Run()
		{
			bool createdNew;
			using (Mutex mutex = new Mutex(true, "Local\\AISwitchDesk.Background.v1", out createdNew))
			{
				if (!createdNew)
				{
					return;
				}
				EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\AISwitchDesk.Background.Stop.v1");
				try
				{
					Task task = Task.Factory.StartNew(delegate
					{
						Guard("source", stop, delegate { Watch(stop); });
					}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
					Task task2 = Task.Factory.StartNew(delegate
					{
						Guard("delivery", stop, delegate { Deliver(stop); });
					}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
					try
					{
						Task.WaitAll(task, task2);
					}
					finally
					{
						stop.Set();
						mutex.ReleaseMutex();
					}
				}
				finally
				{
					if (stop != null)
					{
						((IDisposable)stop).Dispose();
					}
				}
			}
		}

		private static void Watch(WaitHandle stop)
		{
			GptWatcher gptWatcher = new GptWatcher();
			try
			{
			while (!stop.WaitOne(0))
			{
				try
				{
					if (!Enabled)
					{
						gptWatcher.Forget();
					}
					else
					{
						List<IntPtr> list = Windows("ChatGPT");
						gptWatcher.Keep(list);
						if (list.Count > 0)
						{
							List<Handoff> jobs = RelayStore.All();
							foreach (IntPtr item in list)
							{
								try
								{
									gptWatcher.Poll(item, jobs);
								}
								catch (Exception ex)
								{
									Report("source", ex);
								}
							}
						}
					}
				}
				catch (Exception ex2)
				{
					Report("source", ex2);
				}
				stop.WaitOne(1000);
			}
			}
			finally
			{
				gptWatcher.Forget();
			}
		}

		private static void Deliver(WaitHandle stop)
		{
			Outbox outbox = new Outbox();
			Dictionary<string, DateTime> dictionary = new Dictionary<string, DateTime>();
			Dictionary<string, DateTime> defer = new Dictionary<string, DateTime>();
			Dictionary<string, DateTime> navigation = new Dictionary<string, DateTime>();
			string path = Path.Combine(RelayStore.Root, "paired-relay-since.txt");
			long result = DateTime.UtcNow.Ticks;
			try
			{
				if (!File.Exists(path))
				{
					RelayStore.AtomicText(path, result.ToString());
				}
				long saved;
				if (long.TryParse(File.ReadAllText(path), out saved))
				{
					result = saved;
				}
			}
			catch (IOException ex)
			{
				Report("delivery", ex);
			}
			catch (UnauthorizedAccessException ex2)
			{
				Report("delivery", ex2);
			}
			DateTime nextPrune = DateTime.MinValue;
			while (!stop.WaitOne(0))
			{
				Handoff job = null;
				try
				{
					if (DateTime.UtcNow >= nextPrune)
					{
						nextPrune = DateTime.UtcNow.AddHours(6.0);
						try
						{
							RelayStore.Prune(TimeSpan.FromDays(30.0));
						}
						catch (Exception ex4)
						{
							Report("cleanup", ex4);
						}
					}
					if (!Enabled)
					{
						stop.WaitOne(900);
						continue;
					}
					try
					{
						outbox.Scan();
					}
					catch (IOException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
					job = (from x in RelayStore.All()
						where (x.Background || x.Task == null) && !x.Held && (x.Stage == "ready" || x.Stage == "confirm" || x.Stage == "linked" || x.Stage == "await-result" || x.Returning) && (!defer.ContainsKey(x.Id) || defer[x.Id] <= DateTime.UtcNow)
						orderby x.Created
						select x).FirstOrDefault();
					if (job != null)
					{
						if (job.Stage == "linked")
						{
							Handoff handoff = RelayStore.Load(job.LinkedId);
							if (handoff.Stage == "done" || handoff.Stage == "cancel")
							{
								job.Stage = handoff.Stage;
								job.Detail = handoff.Detail;
								job.RoundTrip = handoff.RoundTrip;
								RelayStore.Save(job);
							}
							else
							{
								defer[job.Id] = DateTime.UtcNow.AddSeconds(5.0);
								if (job.Detail != handoff.Detail)
								{
									job.Detail = handoff.Detail;
									RelayStore.Save(job);
								}
							}
							stop.WaitOne(150);
							continue;
						}
						if (!job.Background)
						{
							job.Background = true;
							RelayStore.Save(job);
						}
						if (ConversationRoute.LocalReceipt(job))
						{
							RelayPump.Returned(job);
							continue;
						}
						if (!job.PairChats && !job.FromFolder && job.From == "ChatGPT" && job.Stage == "ready" && job.Task == null && job.Created >= result)
						{
							job.PairChats = true;
							job.RoundTrip = RoundTrips.Requested(job.Instruction);
							job.RequestKey = job.Id;
							RelayStore.Save(job);
						}
						if (job.PairChats)
						{
							PrepareRoute(job);
						}
						string app = (job.Returning ? "ChatGPT" : job.To);
						List<IntPtr> list = Windows(app);
						if (list.Count == 0)
						{
							DateTime value;
							if (!dictionary.TryGetValue(app, out value) || value <= DateTime.UtcNow)
							{
								dictionary[app] = DateTime.UtcNow.AddMinutes(1.0);
								new ClientWindow(app).Launch();
							}
							defer[job.Id] = DateTime.UtcNow.AddSeconds(3.0);
						}
						else
						{
							ChatUi[] views = list.Select((IntPtr h) => new ChatUi(h, () => Enabled && IsClient(h, app) && !stop.WaitOne(0))).ToArray();
							ClientInput.TryUse(app, delegate
							{
								DeliverTo(job, views, navigation);
							});
							defer[job.Id] = DateTime.UtcNow.AddSeconds((!(job.Stage == "await-result")) ? 1 : 5);
						}
					}
				}
				catch (Exception ex3)
				{
					if (job != null && !stop.WaitOne(0))
					{
						// Recording the failure reads and writes the same files that
						// may have just failed. It must not end the delivery loop.
						defer[job.Id] = DateTime.UtcNow.AddSeconds((ex3 is ConversationPending) ? 4 : 8);
						try
						{
							string message = ex3.GetBaseException().Message;
							if (ex3 is ConversationPending || (job.Stage != "confirm" && job.Stage != "return-confirm" && (Retry.IsLater(ex3) || ex3 is ElementNotAvailableException || ex3 is COMException || !Enabled)))
							{
								Handoff handoff2 = RelayStore.Load(job.Id);
								if (handoff2.Revision == job.Revision && handoff2.Detail != message)
								{
									handoff2.Detail = message;
									try
									{
										RelayStore.Save(handoff2);
									}
									catch (InvalidOperationException)
									{
									}
								}
							}
							else
							{
								Report("delivery paused " + job.Id, ex3);
								Hold(job, message);
							}
						}
						catch (Exception ex5)
						{
							Report("delivery", ex3);
							Report("delivery record", ex5);
						}
					}
					else if (!stop.WaitOne(0))
					{
						Report("delivery", ex3);
					}
				}
				stop.WaitOne(850);
			}
		}

		internal static void DeliverTo(Handoff job, ChatUi[] views, Dictionary<string, DateTime> navigation)
		{
			string text = (job.Returning ? "ChatGPT" : job.To);
			string route = (job.Returning ? job.SourceRoute : job.ClaudeRoute);
			ChatUi chatUi = views[0];
			if (route != null)
			{
				chatUi = views.FirstOrDefault((ChatUi v) => ConversationRoute.Matches(v, route, job.Returning ? job.SourceProof : null));
				if (chatUi == null)
				{
					DateTime value;
					if (!navigation.TryGetValue(text, out value) || value <= DateTime.UtcNow)
					{
						ConversationRoute.Navigate(views[0], route);
						navigation[text] = DateTime.UtcNow.AddSeconds(12.0);
					}
					throw new ConversationPending("正在打开本次交接配对的" + text + " 对话。");
				}
				chatUi.ExpectedRoute = route;
				chatUi.ExpectedProof = job.SourceProof;
			}
			else if (job.Stage == "confirm")
			{
				chatUi = views.FirstOrDefault((ChatUi v) => v.TranscriptContains(job.Receipt)) ?? chatUi;
			}
			RelayPump.Advance(job, chatUi);
		}

		internal static void PrepareRoute(Handoff job)
		{
			if (job.SourceRoute == null || (job.SourceRoute.StartsWith("codex://") && job.SourceProof == null))
			{
				string proof;
				string text = ConversationRoute.LocalSession(job.Task, job.RequestKey, job.SourceRoute, out proof);
				if (text != null)
				{
					job.SourceRoute = text;
					job.SourceProof = proof;
					RelayStore.Save(job);
				}
				else if (job.RoundTrip)
				{
					throw new ConversationPending("正在核对发起任务的 GPT 对话。打开原对话后，后台会继续。");
				}
			}
			if (job.Stage == "ready" && job.SourceRoute != null && job.ClaudeRoute == null)
			{
				if (job.PairSnapshot == null)
				{
					job.PairSnapshot = ConversationPairs.Snapshot(job.SourceRoute);
					RelayStore.Save(job);
				}
				string text2 = ConversationRoute.Canonical("Claude", job.PairSnapshot);
				if (text2 != null)
				{
					job.ClaudeRoute = text2;
					RelayStore.Save(job);
				}
				else if (RelayStore.All().Any((Handoff x) => x.Id != job.Id && x.Created < job.Created && x.PairChats && x.SourceRoute != null && x.ClaudeRoute == null && !x.Held && (x.Stage == "ready" || x.Stage == "confirm")))
				{
					throw new ConversationPending("正在等待前一条交接确定 Claude 对话。");
				}
			}
		}
	}
}
