using System;
using System.Windows;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal static class RailMotion
	{
		internal static bool Enabled
		{
			get
			{
				if (!SystemInformation.HighContrast)
				{
					return SystemParameters.ClientAreaAnimation;
				}
				return false;
			}
		}

		internal static float SmoothStep(float value)
		{
			float num = Math.Max(0f, Math.Min(1f, value));
			return num * num * (3f - 2f * num);
		}
	}
}
