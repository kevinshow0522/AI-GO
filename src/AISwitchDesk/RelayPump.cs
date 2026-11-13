using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AISwitchDesk
{
	internal sealed class RelayPump
	{
		private readonly DeskForm desk;

		private DateTime next;

		private bool checking;

		internal volatile bool Busy;

		internal RelayPump(DeskForm owner)
		{
			desk = owner;
		}

		internal async void Tick()
		{
			if (checking || DateTime.UtcNow < next)
			{
				return;
			}
			next = DateTime.UtcNow.AddSeconds(1.8);
			checking = true;
			Handoff job = null;
			try
			{
				List<Handoff> rows = await Task.Run(() => RelayStore.All());
				desk.ObserveHandoffs(rows);
				if (!desk.CanWork)
				{
					return;
				}
				job = (from x in rows
					where !x.Background && x.Task != null && !x.Held && x.Stage != Stages.Done && x.Stage != Stages.Cancel
					orderby (x.Stage == Stages.Writing) ? 1 : 0, x.Created
					select x).FirstOrDefault();
				if (job == null)
				{
					return;
				}
				Busy = true;
				bool source = job.Stage == Stages.Ask || job.Stage == Stages.Writing;
				string app = (source ? job.From : job.To);
				IntPtr window = desk.RelayWindow(app, job.Stage != Stages.Writing);
				if (window == IntPtr.Zero)
				{
					return;
				}
				ChatUi ui = new ChatUi(window, () => Native.IsWindowVisible(window) && BackgroundRelay.IsClient(window, app));
				await Task.Run(() => ClientInput.TryUse(app, delegate
				{
					Advance(job, ui);
				}));
			}
			catch (Exception ex)
			{
				if (job == null)
				{
					return;
				}
				try
				{
					Handoff handoff = RelayStore.Load(job.Id);
					if (handoff.Stage != Stages.Cancel && handoff.Stage != Stages.Done)
					{
						handoff.Held = true;
						handoff.Detail = ex.GetBaseException().Message;
						RelayStore.Save(handoff);
					}
				}
				catch (Exception)
				{
				}
			}
			finally
			{
				Busy = false;
				checking = false;
			}
		}

		internal static void Advance(Handoff j, ChatUi ui)
		{
			if (j.Stage == Stages.Ask || ((j.Stage == Stages.Ready || j.Stage == Stages.ReturnReady) && (!j.UploadStarted || !ui.PickingFile)))
			{
				ui.WaitForEditor();
			}
			switch (j.Stage)
			{
			case Stages.Ask:
				if (ui.HasAttachments(ui.Editor()))
				{
					throw Retry.Later(new InvalidOperationException("目标输入框已有草稿附件，请先处理后再继续。"));
				}
				ui.Send(j.WriterPrompt(), delegate
				{
					j.SourcePage = ui.PageKey();
					j.Stage = Stages.Writing;
					j.Detail = "正在等待写作方输出完整文件。";
					RelayStore.Save(j);
				});
				break;
			case Stages.Writing:
			{
				string text2 = ui.GeneratedFile(j);
				if (text2 == null)
				{
					if (DateTime.UtcNow.Ticks - j.Updated > TimeSpan.FromMinutes(15.0).Ticks)
					{
						throw new InvalidOperationException("尚未找到完整文件。请打开原来的写作对话，再点继续。");
					}
				}
				else
				{
					RelayStore.AtomicText(RelayStore.FileOf(j), text2);
					j.Stage = Stages.Ready;
					j.Detail = "文件已生成。";
					RelayStore.Save(j);
				}
				break;
			}
			case Stages.AwaitResult:
				RoundTrips.Observe(j, ui);
				break;
			case Stages.ReturnReady:
			case Stages.Ready:
			{
				bool returning = j.Returning;
				if (!returning && j.ClaudeRoute == null && !ui.FreshConversation(j))
				{
					break;
				}
				string text = ui.PageKey();
				ui.CheckRoute();
				if (!returning && ui.ExpectedRoute == null && !string.IsNullOrEmpty(j.TargetPage) && !string.IsNullOrEmpty(text) && j.TargetPage != text)
				{
					throw new InvalidOperationException("接收方已切换聊天。请返回原目标对话再继续。");
				}
				if (!returning && string.IsNullOrEmpty(j.TargetPage))
				{
					j.TargetPage = text;
					RelayStore.Save(j);
				}
				string receipt = j.Receipt;
				if (ui.TranscriptContains(receipt) && ui.Posted(receipt))
				{
					Delivered(j, ui);
					break;
				}
				ui.Attach(j);
				ui.Send(j.ReceiverPrompt(), delegate
				{
					ui.VerifyAttachment(j);
					j.Stage = (returning ? Stages.ReturnConfirm : Stages.Confirm);
					j.Detail = "正在核对聊天发送回执。";
					RelayStore.Save(j);
				});
				if (ChatUi.Until(() => (!ui.Posted(receipt)) ? null : "sent", 8500) != null)
				{
					Delivered(j, ui);
				}
				break;
			}
			case Stages.ReturnConfirm:
			case Stages.Confirm:
				ui.CheckRoute();
				if (ui.Posted(j.Receipt))
				{
					Delivered(j, ui);
					break;
				}
				if (DateTime.UtcNow.Ticks - j.Updated < TimeSpan.FromMinutes(2.0).Ticks)
				{
					throw new ConversationPending("已提交，正在等待目标对话显示发送回执。");
				}
				throw new InvalidOperationException("已尝试发送。请在目标对话核对附件，点继续可重新检查回执。");
			}
		}

		private static void Delivered(Handoff sent, ChatUi ui)
		{
			Handoff handoff = RelayStore.Load(sent.Id);
			if (handoff.Stage == Stages.Done || handoff.Stage == Stages.Cancel)
			{
				return;
			}
			if (handoff.Returning)
			{
				ui.CheckRoute();
				Returned(handoff);
				return;
			}
			if (handoff.PairChats && handoff.SourceRoute != null)
			{
				string text = ConversationRoute.Canonical("Claude", ui.PageKey());
				if (text == null || (handoff.ClaudeRoute != null && handoff.ClaudeRoute != text))
				{
					throw new ConversationPending("已发送，正在等候 Claude 提供本次聊天的地址。");
				}
				handoff.ClaudeRoute = text;
				ConversationPairs.Set(handoff.SourceRoute, text, false, handoff.PairSnapshot);
			}
			handoff.Stage = ((!handoff.RoundTrip) ? Stages.Done : ((handoff.ReturnHash != null) ? Stages.ReturnReady : Stages.AwaitResult));
			handoff.Detail = (handoff.RoundTrip ? "Claude 已收到原件，修改完成后将自动回到原 GPT 对话。" : (handoff.To + " 的聊天已显示交接回执。"));
			handoff.DownloadStarted = 0L;
			handoff.DownloadExisting = null;
			handoff.DownloadName = null;
			handoff.DownloadPreviewClicked = false;
			handoff.Held = false;
			RelayStore.Save(handoff);
		}

		internal static void Returned(Handoff j)
		{
			j.Stage = Stages.Done;
			j.Held = false;
			j.Detail = "Claude 的修改稿已送回发起任务的 GPT 对话。";
			RelayStore.Save(j);
		}
	}
}
