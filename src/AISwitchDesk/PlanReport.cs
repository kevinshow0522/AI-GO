using System.Collections.Generic;

namespace AISwitchDesk
{
	public sealed class PlanReport
	{
		public string Source;

		public string Note;

		public long ReadAt;

		public List<Allowance> Rows = new List<Allowance>();
	}
}
