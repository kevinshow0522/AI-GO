using System;

namespace AISwitchDesk
{
	internal sealed class ConversationPending : InvalidOperationException
	{
		internal ConversationPending(string reason)
			: base(reason)
		{
		}
	}
}
