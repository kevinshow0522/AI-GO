using System;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AISwitchDesk
{
    // Starts the sidebar when the person signs in to Windows, through the
    // per-user Run key. Nothing here needs administrator rights.
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AI Switch Desk";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        // The program that starts at sign-in, or null when none is set.
        internal static string Registered
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string value = key == null ? null : key.GetValue(ValueName) as string;
                        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
                    }
                }
                catch (SecurityException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                catch (IOException) { return null; }
            }
        }

        // True when this copy of the sidebar starts at sign-in.
        internal static bool Enabled
        {
            get
            {
                string registered = Registered;
                return registered != null && string.Equals(registered, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Turning it on points sign-in at this copy, replacing any other.
        internal static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
