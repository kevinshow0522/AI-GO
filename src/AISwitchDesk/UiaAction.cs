using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace AISwitchDesk
{
    // .NET Framework's managed UIA client does not expose the LegacyIAccessible
    // pattern. Use the native client for this one operation, binding by runtime
    // id inside the already verified owner window, never by screen position.
    internal static class UiaAction
    {
        internal static bool TryInvokeDefault(IntPtr window, int[] runtimeId)
        {
            if (runtimeId == null || runtimeId.Length == 0) return false;
            object client = null, condition = null, pattern = null;
            IElement root = null, target = null;
            try
            {
                client = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                var automation = (IAutomation)client;
                root = automation.ElementFromHandle(window);
                condition = automation.CreatePropertyCondition(30000, runtimeId);
                target = root.FindFirst(7, condition);
                if (target == null || !runtimeId.SequenceEqual(target.GetRuntimeId())) return false;
                pattern = target.GetCurrentPattern(10018);
                var legacy = pattern as ILegacy;
                if (legacy == null) { DropTrace.Write("ui", "default-action-unavailable"); return false; }
                DropTrace.Write("ui", "default-action-target=" + legacy.GetCurrentName());
                legacy.DoDefaultAction();
                DropTrace.Write("ui", "default-action-invoked");
                return true;
            }
            finally
            {
                Release(pattern); Release(target); Release(condition); Release(root); Release(client);
            }
        }

        static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        // Method order follows Microsoft's UIAutomationClient.h. Unused slots
        // are retained so the called methods keep their native vtable offsets.
        [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAutomation
        {
            void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
            IElement ElementFromHandle(IntPtr window);
            void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
            void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
            void CreateTreeWalker(); void ControlViewWalker(); void ContentViewWalker(); void RawViewWalker();
            void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition(); void CreateCacheRequest();
            void CreateTrueCondition(); void CreateFalseCondition();
            [return: MarshalAs(UnmanagedType.Interface)]
            object CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
        }

        [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IElement
        {
            void SetFocus();
            [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
            int[] GetRuntimeId();
            IElement FindFirst(int scope, [MarshalAs(UnmanagedType.Interface)] object condition);
            void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
            void GetCurrentPropertyValue(); void GetCurrentPropertyValueEx(); void GetCachedPropertyValue();
            void GetCachedPropertyValueEx(); void GetCurrentPatternAs(); void GetCachedPatternAs();
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetCurrentPattern(int patternId);
        }

        [ComImport, Guid("828055ad-355b-4435-86d5-3b51c14a9b1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ILegacy
        {
            void Select(int flags);
            void DoDefaultAction();
            void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
            int GetCurrentChildId();
            [return: MarshalAs(UnmanagedType.BStr)]
            string GetCurrentName();
        }
    }
}
