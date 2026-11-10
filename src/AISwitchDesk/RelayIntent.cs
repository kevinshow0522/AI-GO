using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AISwitchDesk
{
	internal sealed class RelayIntent
	{
		private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

		internal string Text;

		internal string Name;

		internal string Instruction;

		internal string LocalPath;

		internal bool Create;

		internal bool Previous;

		internal static readonly string Extensions = "txt|md|csv|tsv|json|xml|ya?ml|html?|py|js|ts|cs|css|sql|r|sh|pdf|docx?|xlsx?|pptx?|odt|rtf|epub|png|jpe?g|webp|gif|zip";

		private static readonly Regex PathToken = new Regex("(?<path>[A-Z]:[\\\\/][^\\r\\n\"“”'<>|，。！？；]+?\\.(?:" + Extensions + "))(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		internal static string Fingerprint(string text)
		{
			using (SHA256 sHA = SHA256.Create())
			{
				return BitConverter.ToString(sHA.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 16).Replace("-", "").ToLowerInvariant();
			}
		}

		internal static bool IsText(string name)
		{
			return Regex.IsMatch(Path.GetExtension(name ?? ""), "^\\.(?:txt|md|csv|tsv|json|xml|ya?ml|html?|py|js|ts|cs|css|sql|r|sh)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		internal static bool IsFileName(string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
			{
				return false;
			}
			try
			{
				return RelayStore.SafeName(name) == name && Regex.IsMatch(name, "\\.(?:" + Extensions + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			}
			catch (ArgumentException)
			{
				return false;
			}
		}

		internal static string NamedFile(string text)
		{
			Match match = Regex.Match(text, "[「“\"‘'《\\x60]([^」”\"’'》\\x60\\r\\n\\\\/]{1,100}\\.(?:" + Extensions + "))[」”\"’'》\\x60]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (match.Success && IsFileName(match.Groups[1].Value))
			{
				return match.Groups[1].Value;
			}
			Match match2 = Regex.Match(text, "(?:命名为|文件名(?:为|叫)?|名为|叫做|named?|called)\\s*([A-Za-z0-9_\\-\\p{IsCJKUnifiedIdeographs}][A-Za-z0-9_.\\-\\p{IsCJKUnifiedIdeographs}]*\\.(?:" + Extensions + "))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (match2.Success && IsFileName(match2.Groups[1].Value))
			{
				return match2.Groups[1].Value;
			}
			Match match3 = Regex.Match(text, "(?<![A-Za-z0-9_.\\-])([A-Za-z0-9_\\-][A-Za-z0-9_.\\-]*\\.(?:" + Extensions + "))(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (match3.Success && IsFileName(match3.Groups[1].Value))
			{
				return match3.Groups[1].Value;
			}
			Match match4 = Regex.Match(text, "(?:把|将|將)(?:文件|附件)?\\s*([\\p{IsCJKUnifiedIdeographs}\\w.\\-]+?\\.(?:" + Extensions + "))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (!match4.Success || !IsFileName(match4.Groups[1].Value))
			{
				return null;
			}
			return match4.Groups[1].Value;
		}

		internal static RelayIntent Parse(string value)
		{
			if (string.IsNullOrWhiteSpace(value) || value.Length > 15000)
			{
				return null;
			}
			string text = value.Trim();
			if (Regex.IsMatch(text, "〔双窗(?:回传|交接) [a-f0-9]{10,32}\\b") || text.StartsWith("双窗取件 "))
			{
				return null;
			}
			if (Regex.IsMatch(text, "(?:验证|实测|测试).{0,8}吗[？?][\\s\\S]*\\n(?:实测一次|只用本机|可以，实测|不测试)|^<send_user_message_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			{
				return null;
			}
			if (!Regex.IsMatch(text, "claude|克劳德|克勞德", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			{
				return null;
			}
			string text2 = "(?:Claude(?:\\s+AI)?|克劳德|克勞德)";
			string input = Regex.Replace(text, "[“「](?:[^”」]*?)[”」]|\"[^\"]+\"|「[^」]+」", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (Regex.IsMatch(input, "(?:不要|别|禁止|取消|不用|不必|无需|先不|暂不|暫不|請勿|別|don't|do not|never)\\s*.{0,30}(?:发|传|傳|送|给|交|send|forward)|(?:如果|假如|比如|例如|举例|当我说|我在.{0,7}说|怎么实现|如何实现|制作一个.{0,10}后台|自动发送.{0,3}功能|when I (?:say|ask)|for example|(?:explain|show me) how|how (?:do|would|can) I)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			{
				return null;
			}
			if (!Regex.IsMatch(input, "(?:发(?:送|送过去)?|轉發|转发|投递|递|寄|交|传|傳|送|分享|給|给)\\s*(?:送?给|至|到|給|往|一份给|一份給)?\\s*" + text2, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !Regex.IsMatch(input, "\\b(?:send|forward|pass|give|deliver|share|hand\\s*off)\\b.{0,140}?\\b(?:to|with)\\s+" + text2 + "|\\b(?:send|give)\\s+" + text2 + "\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !Regex.IsMatch(input, "^\\s*(?:(?:请|麻烦|帮我|现在)\\s*)?(?:(?:把|将|这份|这个).{0,45}?)?(?:交由|让|請|请|讓)\\s*" + text2 + "\\s*(?:帮我|幫我)?(?:修改|修订|编辑|润色|审稿|审一遍|校对|改一下|改好)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			{
				return null;
			}
			Match match = PathToken.Match(text);
			string text3 = (match.Success ? Path.GetFileName(match.Groups["path"].Value) : NamedFile(text));
			if (text3 == null && !Regex.IsMatch(text, "文件|附件|文档|檔案|文檔|稿子|报告|方案|脚本|文稿|代码|表格|计划书|PDF|Word|Excel|PPT|Markdown|图片|照片|\\b(?:file|attachment|document|draft|report|script|spreadsheet|presentation|it|this)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			{
				return null;
			}
			bool create = Regex.IsMatch(text, "(?:写|生成|做|制作|拟|整理|起草|导出|正文[是为：:]|内容[是为：:]|產生|撰寫|write|create|generate|draft|prepare|make|export)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			Match match2 = Regex.Match(text, "(?:让|请|讓|請)\\s*(?:Claude|他|它|克劳德|克勞德)\\s*(?:来|幫我|帮我)?\\s*(.+)$|\\bask\\s+(?:Claude|it|them)\\s+to\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
			Match match3 = Regex.Match(text, text2 + "\\s*(?:来|帮我|幫我)?\\s*((?:修改|改|修订|润色|校对|审|批注|优化|检查|翻译|完善|编辑).+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
			string instruction = (match2.Success ? match2.Groups.Cast<Group>().Skip(1).First((Group g) => g.Success)
				.Value.Trim() : (match3.Success ? match3.Groups[1].Value.Trim() : (RoundTrips.Requested(text) ? ("请按这条要求处理附件。\n" + text) : "请阅读附件。")));
			RelayIntent relayIntent = new RelayIntent();
			relayIntent.Text = text;
			relayIntent.Name = text3;
			relayIntent.LocalPath = (match.Success ? match.Groups["path"].Value : null);
			relayIntent.Create = create;
			relayIntent.Previous = text3 != null || Regex.IsMatch(text, "这份|这个|这张|这篇|這份|這個|那份|那个|附件|刚才|刚刚|上一个|上面|刚生成|剛才|previous|last file|just created|\\b(?:this|that|it)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			relayIntent.Instruction = instruction;
			return relayIntent;
		}
	}
}
