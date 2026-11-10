using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal sealed class GptFile
	{
		internal string Name;

		internal string LocalPath;

		internal AutomationElement Element;

		internal static GptFile From(string label, string value, string css, ControlType kind, AutomationElement element)
		{
			string name = null;
			string text = null;
			if (!string.IsNullOrWhiteSpace(value))
			{
				string text2 = Uri.UnescapeDataString(value.Trim());
				if (Regex.IsMatch(text2, "^/?[A-Za-z]:[/\\\\]"))
				{
					text = text2.TrimStart('/').Replace('/', '\\');
				}
				else
				{
					Uri result;
					if (!Uri.TryCreate(value, UriKind.Absolute, out result))
					{
						return null;
					}
					if (result.IsFile && !result.IsUnc)
					{
						text = result.LocalPath;
					}
					else if (result.Scheme == "sandbox" && (result.AbsolutePath.StartsWith("/mnt/data/") || result.AbsolutePath.StartsWith("/workspace/")))
					{
						name = Path.GetFileName(Uri.UnescapeDataString(result.AbsolutePath));
					}
					else
					{
						if (!(result.Scheme == "https") || (!(result.Host == "chatgpt.com") && !result.Host.EndsWith(".oaiusercontent.com", StringComparison.OrdinalIgnoreCase)) || (!result.AbsolutePath.StartsWith("/backend-api/files/") && !result.AbsolutePath.StartsWith("/backend-api/estuary/") && !result.Host.EndsWith(".oaiusercontent.com", StringComparison.OrdinalIgnoreCase)))
						{
							return null;
						}
						name = Path.GetFileName(Uri.UnescapeDataString(result.AbsolutePath));
					}
				}
			}
			else if (kind != ControlType.Button || !Regex.IsMatch(label + " " + css, "下载|下載|download|file-card|artifact|file-pill", RegexOptions.IgnoreCase))
			{
				return null;
			}
			if (text != null)
			{
				name = Path.GetFileName(text);
			}
			if (!RelayIntent.IsFileName(name))
			{
				string text3 = Regex.Replace(label, "^(?:下载|下載|Download)\\s*[:：]?\\s*", "", RegexOptions.IgnoreCase).Trim(' ', '《', '》', '「', '」');
				name = (RelayIntent.IsFileName(text3) ? text3 : RelayIntent.NamedFile(label));
			}
			if (!RelayIntent.IsFileName(name))
			{
				return null;
			}
			GptFile gptFile = new GptFile();
			gptFile.Name = name;
			gptFile.LocalPath = text;
			gptFile.Element = element;
			return gptFile;
		}
	}
}
