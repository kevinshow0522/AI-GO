using System;
using System.Runtime.InteropServices;

namespace AISwitchDesk
{
	[Guid("2FC57384-A068-44D7-A331-30982FCF7177")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[ComVisible(true)]
	public interface IDirectEffect
	{
		void GetEffectId(out Guid id);

		void GetNamedPropertyMapping([MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, out uint mapping);

		void GetPropertyCount(out uint count);

		void GetProperty(uint index, out IntPtr property);

		void GetSource(uint index, out IntPtr source);

		void GetSourceCount(out uint count);
	}
}
