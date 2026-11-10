using System;
using System.IO;

namespace AISwitchDesk
{
    internal static class DropTrace
    {
        private static readonly object sync = new object();
        internal static void Write(string app, string stage)
        {
            try
            {
                lock (sync)
                {
                    string path = Path.Combine(RelayStore.Root, "file-drop.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 512000) File.WriteAllText(path, "");
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + app + " " + stage + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
