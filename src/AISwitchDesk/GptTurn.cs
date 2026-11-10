using System.Collections.Generic;
using System.Windows.Automation;

namespace AISwitchDesk
{
	internal sealed class GptTurn
	{
		internal readonly List<string> User = new List<string>();

		internal readonly List<string> Answer = new List<string>();

		internal readonly List<GptFile> UserFiles = new List<GptFile>();

		internal readonly List<GptFile> Files = new List<GptFile>();

		internal string Key;

		internal string Prompt;

		internal string ElementId;

		internal AutomationElement FirstAnswer;

		internal AutomationElement LastAnswer;

		internal string Verbatim;

		internal bool Assistant;

		internal bool Completed;

		internal bool ToolHandoff;

		internal string Body
		{
			get
			{
				return Verbatim ?? string.Join("\n", Answer).Trim();
			}
		}
	}
}
