using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace AISwitchDesk
{
    // Forward a user's released file list through the destination window's OLE
    // registration. The endpoint is resolved by COM in its owning process.
    internal static class ComposerFileDrop
    {
        private static readonly Guid DropProtocol = new Guid("00000160-0000-0000-c000-000000000046");
        private static readonly Guid DataObject = new Guid("0000010e-0000-0000-c000-000000000046");
        private static readonly Guid ShellDataObject = new Guid("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
        private const uint Copy = 1;

        [ComImport, Guid("00000160-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropEndpoint
        {
            [PreserveSig] int DragDrop(IntPtr reserved, IntPtr window, IntPtr marshaledData, uint operation,
                uint keys, long screenPoint, ref uint effect, uint sourceThread, IntPtr localData, IntPtr reserved2);
            [PreserveSig] int IsBrokeredTarget(out int brokered);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        }

        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] private static extern void OleUninitialize();
        [DllImport("combase.dll")] private static extern int InternalGetWindowPropInterface2(
            IntPtr window, IntPtr endpointId, int checkStrongNames, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IDropEndpoint endpoint, out int local);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(
            string name, IntPtr context, out IntPtr item, uint attributes, IntPtr actualAttributes);
        [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromIDLists(uint count,
            [In, MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)] IntPtr[] items, out IShellItemArray array);
        [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr memory, bool release, out IStream stream);
        [DllImport("ole32.dll")] private static extern int CoMarshalInterface(IStream stream, ref Guid iid, IntPtr value,
            uint context, IntPtr contextData, uint flags);
        [DllImport("ole32.dll")] private static extern int CoReleaseMarshalData(IStream stream);

        private static IntPtr TargetAt(IntPtr root, int x, int y)
        {
            Native.Rect bounds;
            if (!Native.IsWindow(root) || !Native.IsWindowEnabled(root) || !Native.GetWindowRect(root, out bounds) ||
                x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
                throw new InvalidOperationException("目标聊天输入框当前无法接收文件。");
            IntPtr hit = root;
            for (int i = 0; i < 16; i++)
            {
                var local = new Native.Point { X=x, Y=y };
                if (!Native.ScreenToClient(hit, ref local)) break;
                IntPtr child = Native.ChildWindowFromPointEx(hit, local, 7);
                if (child == IntPtr.Zero || child == hit) break;
                hit = child;
            }
            while (hit != IntPtr.Zero)
            {
                // Both properties are opaque; never interpret a foreign pointer.
                if (GetProp(hit, "OleDropTargetInterface") != IntPtr.Zero && GetProp(hit, "OleEndPointID") != IntPtr.Zero)
                    return hit;
                if (hit == root) break;
                hit = GetParent(hit);
            }
            throw new NotSupportedException("聊天窗口没有注册可接收文件的拖放入口。");
        }

        private static IntPtr CreateFiles(string[] files)
        {
            var ids = new IntPtr[files.Length];
            IShellItemArray items = null;
            try
            {
                for (int i=0; i<files.Length; i++)
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetFullPath(files[i]), IntPtr.Zero, out ids[i], 0, IntPtr.Zero));
                Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)ids.Length, ids, out items));
                Guid handler = ShellDataObject, iid = DataObject;
                IntPtr data;
                Marshal.ThrowExceptionForHR(items.BindToHandler(IntPtr.Zero, ref handler, ref iid, out data));
                return data;
            }
            finally
            {
                if (items != null) Marshal.ReleaseComObject(items);
                foreach (IntPtr id in ids) if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id);
            }
        }

        // A table-strong standard COM marshal keeps delayed shell formats
        // available throughout DragEnter, DragOver and Drop.
        private sealed class FilePacket : IDisposable
        {
            private IStream stream;
            private bool registered;
            internal IntPtr Data;

            internal FilePacket(string[] files)
            {
                IntPtr value = CreateFiles(files);
                try
                {
                    Guid iid = DataObject;
                    Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out stream));
                    Marshal.ThrowExceptionForHR(CoMarshalInterface(stream, ref iid, value, 0, IntPtr.Zero, 1));
                    registered = true;
                    System.Runtime.InteropServices.ComTypes.STATSTG stat;
                    stream.Stat(out stat, 1);
                    byte[] bytes = new byte[checked((int)stat.cbSize)];
                    stream.Seek(0, 0, IntPtr.Zero);
                    stream.Read(bytes, bytes.Length, IntPtr.Zero);
                    Data = Marshal.AllocCoTaskMem(checked(bytes.Length+4));
                    Marshal.WriteInt32(Data, bytes.Length);
                    Marshal.Copy(bytes,0,IntPtr.Add(Data,4),bytes.Length);
                }
                catch { Dispose(); throw; }
                finally { Marshal.Release(value); }
            }

            public void Dispose()
            {
                if (Data != IntPtr.Zero) { Marshal.FreeCoTaskMem(Data); Data = IntPtr.Zero; }
                if (stream == null) return;
                try
                {
                    if (registered) { stream.Seek(0,0,IntPtr.Zero); CoReleaseMarshalData(stream); }
                }
                finally { Marshal.ReleaseComObject(stream); stream=null; }
            }
        }

        internal static void Deliver(IntPtr root, int x, int y, string[] files, Action verify)
        {
            DeliverTracked(root, x, y, files, verify, null);
        }

        internal static void DeliverTracked(IntPtr root, int x, int y, string[] files, Action verify, Action beforeDrop)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                DeliverInApartment(root,x,y,files,verify,beforeDrop);
                return;
            }
            var result = new TaskCompletionSource<bool>();
            var thread = new Thread(delegate()
            {
                try { DeliverInApartment(root,x,y,files,verify,beforeDrop); result.SetResult(true); }
                catch (Exception failure) { result.SetException(failure); }
            }) { IsBackground = true, Name = "Chat attachment drop" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            result.Task.GetAwaiter().GetResult();
        }

        private static void DeliverInApartment(IntPtr root, int x, int y, string[] files, Action verify, Action beforeDrop)
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
            IDropEndpoint endpoint = null;
            try
            {
                verify();
                IntPtr target = TargetAt(root,x,y);
                Guid iid = DropProtocol;
                int local;
                Marshal.ThrowExceptionForHR(InternalGetWindowPropInterface2(target,GetProp(target,"OleEndPointID"),1,
                    ref iid,out endpoint,out local));
                int brokered;
                Marshal.ThrowExceptionForHR(endpoint.IsBrokeredTarget(out brokered));
                if (brokered != 0) throw new NotSupportedException("目标窗口要求通过系统拖放代理处理文件。");
                uint sourceThread = GetCurrentThreadId();
                long point = ((long)y << 32) | (uint)x;
                using (var packet = new FilePacket(files))
                {
                    uint effect = Copy;
                    Marshal.ThrowExceptionForHR(endpoint.DragDrop(IntPtr.Zero,target,packet.Data,1,1,point,ref effect,sourceThread,IntPtr.Zero,IntPtr.Zero));
                    bool completed = false;
                    try
                    {
                        // Chromium obtains its renderer's dragover decision asynchronously.
                        for (int i=0; i<30; i++)
                        {
                            Thread.Sleep(55);
                            effect=Copy;
                            Marshal.ThrowExceptionForHR(endpoint.DragDrop(IntPtr.Zero,target,IntPtr.Zero,2,1,point,ref effect,sourceThread,IntPtr.Zero,IntPtr.Zero));
                            if ((effect & Copy) != 0 && i > 0) break;
                        }
                        if ((effect & Copy) == 0) throw new NotSupportedException("聊天输入框未接受此次文件拖放。");
                        verify();
                        if (TargetAt(root,x,y) != target) throw new InvalidOperationException("聊天窗口发生变化，文件尚未投递。");
                        if (beforeDrop != null) beforeDrop();
                        effect=Copy;
                        Marshal.ThrowExceptionForHR(endpoint.DragDrop(IntPtr.Zero,target,IntPtr.Zero,4,0,point,ref effect,sourceThread,IntPtr.Zero,IntPtr.Zero));
                        completed = true;
                        DropTrace.Write("ui","native-drop-return effect="+effect+" files="+files.Length);
                    }
                    finally
                    {
                        if (!completed) endpoint.DragDrop(IntPtr.Zero,target,IntPtr.Zero,3,0,point,ref effect,sourceThread,IntPtr.Zero,IntPtr.Zero);
                    }
                }
            }
            finally
            {
                if (endpoint != null) Marshal.ReleaseComObject(endpoint);
                OleUninitialize();
            }
        }
    }
}
