using System;
using System.Runtime.InteropServices;

namespace AISwitchDesk
{
	[ComImport]
	[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
	[Guid("629BDBC8-D932-4FF4-96B9-8D96C5C1E858")]
	internal interface IPropertyBoxes
	{
		void CreateEmpty(out IntPtr box);

		void CreateUInt8(byte value, out IntPtr box);

		void CreateInt16(short value, out IntPtr box);

		void CreateUInt16(ushort value, out IntPtr box);

		void CreateInt32(int value, out IntPtr box);

		void CreateUInt32(uint value, out IntPtr box);

		void CreateInt64(long value, out IntPtr box);

		void CreateUInt64(ulong value, out IntPtr box);

		void CreateSingle(float value, out IntPtr box);
	}
}
