using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Effects;

namespace AISwitchDesk
{
	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	public sealed class FrostEffect : IGraphicsEffect, IGraphicsEffectSource, IDirectEffect
	{
		private readonly IGraphicsEffectSource input;

		private static readonly string[] properties = new string[3] { "StandardDeviation", "Optimization", "BorderMode" };

		public string Name { get; set; }

		internal FrostEffect(IGraphicsEffectSource input)
		{
			this.input = input;
			Name = "Frost";
		}

		public void GetEffectId(out Guid id)
		{
			id = new Guid("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
		}

		public void GetNamedPropertyMapping(string name, out uint index, out uint mapping)
		{
			int num = Array.IndexOf(properties, name);
			if (num < 0)
			{
				throw new ArgumentException("Unknown blur property");
			}
			index = (uint)num;
			mapping = 1u;
		}

		public void GetPropertyCount(out uint count)
		{
			count = 3u;
		}

		public void GetProperty(uint index, out IntPtr property)
		{
			if (index >= 3)
			{
				throw new ArgumentOutOfRangeException("index");
			}
			IPropertyBoxes propertyBoxes = (IPropertyBoxes)WindowsRuntimeMarshal.GetActivationFactory(typeof(PropertyValue));
			IntPtr box;
			if (index == 0)
			{
				propertyBoxes.CreateSingle(9f, out box);
			}
			else
			{
				propertyBoxes.CreateUInt32(1u, out box);
			}
			Guid iid = new Guid("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
			try
			{
				Marshal.ThrowExceptionForHR(Marshal.QueryInterface(box, ref iid, out property));
			}
			finally
			{
				Marshal.Release(box);
			}
		}

		public void GetSource(uint index, out IntPtr source)
		{
			if (index != 0)
			{
				throw new ArgumentOutOfRangeException("index");
			}
			source = Marshal.GetComInterfaceForObject(input, typeof(IGraphicsEffectSource));
		}

		public void GetSourceCount(out uint count)
		{
			count = 1u;
		}

	}
}
