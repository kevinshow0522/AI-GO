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

        private const string TrayArgument = "--tray";

        private static string Stored
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string value = key == null ? null : key.GetValue(ValueName) as string;
                        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    }
                }
                catch (SecurityException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                catch (IOException) { return null; }
            }
        }

        // The program that starts at sign-in, or null when none is set.
        internal static string Registered
        {
            get
            {
                string command = Stored;
                if (command == null) return null;
                if (command.StartsWith("\""))
                {
                    int end = command.IndexOf('"', 1);
                    return end > 1 ? command.Substring(1, end - 1) : command.Trim('"');
                }
                int space = command.IndexOf(" --", StringComparison.Ordinal);
                return space > 0 ? command.Substring(0, space) : command;
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

        // True when sign-in starts it with the rail in the tray.
        internal static bool InTray
        {
            get
            {
                string command = Stored;
                return Enabled && command != null && command.EndsWith(" " + TrayArgument, StringComparison.Ordinal);
            }
        }

        // Turning it on points sign-in at this copy, replacing any other.
        internal static void Set(bool enabled, bool inTray)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"" + (inTray ? " " + TrayArgument : ""), RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
