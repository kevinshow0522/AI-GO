using System;
using System.IO;

namespace AISwitchDesk
{
	public sealed class Handoff
	{
		public string Id;

		public string From;

		public string To;

		public string Name;

		public string Task;

		public string Instruction;

		public string Stage;

		public string Detail;

		public string SourcePage;

		public string TargetPage;

		public bool Held;

		public bool AttachmentStarted;

		public bool NewChat;

		public bool RequestedNew;

		public bool FreshReady;

		public bool Background;

		public string Origin;

		public string RequestedName;

		public string DownloadName;

		public string[] DownloadExisting;

		public bool DownloadPreviewClicked;

		public string OriginElement;

		public string LinkedId;

		public long OriginWindow;

		public bool WriterFollowup;

		public bool PairChats;

		public bool RoundTrip;

		public bool ReturnAttachmentStarted;

		// Older records used the file picker. A receipt scopes native delivery to one leg.
		public string NativeDropReceipt;

		public bool FromFolder;

		public string SourceRoute;

		public string SourceProof;

		public string ClaudeRoute;

		public string PairSnapshot;

		public string RequestKey;

		public string ReturnName;

		public string ReturnHash;

		public long DownloadStarted;

		public long Created;

		public long Updated;

		public int Revision;

		internal bool Returning
		{
			get
			{
				if (!(Stage == Stages.ReturnReady))
				{
					return Stage == Stages.ReturnConfirm;
				}
				return true;
			}
		}

		internal string SendingName
		{
			get
			{
				if (!Returning)
				{
					return Name;
				}
				return ReturnName;
			}
		}

		internal bool UploadStarted
		{
			get
			{
				if (!Returning)
				{
					return AttachmentStarted;
				}
				return ReturnAttachmentStarted;
			}
			set
			{
				if (Returning)
				{
					ReturnAttachmentStarted = value;
				}
				else
				{
					AttachmentStarted = value;
				}
			}
		}

		internal string Begin
		{
			get
			{
				return "DUO_BEGIN_" + Id;
			}
		}

		internal string End
		{
			get
			{
				return "DUO_END_" + Id;
			}
		}

		internal string Tag
		{
			get
			{
				return "双窗交接 " + Id.Substring(0, 10);
			}
		}

		internal string Receipt
		{
			get
			{
				if (!Returning)
				{
					return Tag;
				}
				return "双窗回传 " + Id;
			}
		}

		internal string SuggestedResult
		{
			get
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(Name);
				return fileNameWithoutExtension.Substring(0, Math.Min(fileNameWithoutExtension.Length, 60)) + "-修订-" + Id.Substring(0, 10) + Path.GetExtension(Name);
			}
		}

		internal string WriterPrompt()
		{
			return Task + "\n\n请为 " + To + " 写一份 UTF-8 文本文件「" + Name + "」。双窗会保存并转发它。\n请在一个纯文本代码块中输出完整正文。第一行是 DUO_BEGIN_ 紧接编号，最后一行是 DUO_END_ 紧接相同编号，中间保留文件原文。\n编号为 " + Id + "。两个标记都独占一行，前缀和编号之间没有空格。";
		}

		internal string ReceiverPrompt()
		{
			if (Returning)
			{
				return "这是 Claude 完成的修改稿「" + ReturnName + "」，请在本对话中查收。\n\n〔" + Receipt + "〕";
			}
			string text = Instruction + "\n\n〔" + Tag + " · 来自 " + From + "〕";
			if (RoundTrip)
			{
				string text2 = text;
				text = text2 + "\n完成修改后，请用双窗的 return_file 工具交回成品，交接编号 " + Id + "。如无法使用工具，请在本条回复附上一个可下载的最终文件，命名为「" + SuggestedResult + "」。纯文本也可以放进代码块，首行写 DUO_RETURN_BEGIN_ 紧接交接编号，末行写 DUO_RETURN_END_ 紧接同一编号。";
			}
			return text;
		}

		internal string StageLabel()
		{
			if (Held)
			{
				if (!(Stage == Stages.Confirm) && !(Stage == Stages.ReturnConfirm))
				{
					return "待处理";
				}
				return "待核对";
			}
			switch (Stage)
			{
			case Stages.Capture:
				return "等候 GPT 文件";
			case Stages.Linked:
				return "等候投递回执";
			case Stages.Ask:
				return "等候写作";
			case Stages.Writing:
				return "等待文件";
			case Stages.Ready:
				return "等待转发";
			case Stages.Confirm:
				return "核对送达";
			case Stages.AwaitResult:
				return "等候 Claude 修改";
			case Stages.ReturnReady:
				return "等待回传";
			case Stages.ReturnConfirm:
				return "核对回传";
			case Stages.Paired:
				return "已配对";
			case Stages.Unpaired:
				return "已解除配对";
			case Stages.PairMissing:
				return "未能配对";
			case Stages.Done:
				if (!RoundTrip)
				{
					return "已送达";
				}
				return "已回到原对话";
			case Stages.Cancel:
				return "已取消";
			default:
				return "待处理";
			}
		}
	}
}
