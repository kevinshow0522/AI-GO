using System;
using System.IO;
using System.Linq;

namespace AISwitchDesk
{
    // The single place that decides which installed desktop packages count as
    // "ChatGPT" or "Claude". Windows derives the publisher suffix of a package
    // family from its signing certificate, so another vendor cannot claim it.
    internal static class ClientApps
    {
        internal const string ClaudeFamily = "Claude_pzs8sxrjxfjjc";

        private const string OpenAIPublisher = "_2p2nqsd0c76g0";

        // Known OpenAI desktop packages in launch order: family, entry point.
        private static readonly string[][] Gpt =
        {
            new[] { "OpenAI.Codex" + OpenAIPublisher, "App" },
            new[] { "OpenAI.ChatGPT-Desktop" + OpenAIPublisher, "ChatGPT" }
        };

        private static readonly string[] GptNames = { "OpenAI.Codex", "OpenAI.ChatGPT" };

        internal static bool Owns(string app, string family)
        {
            if (string.IsNullOrEmpty(family)) return false;
            if (app == "Claude") return string.Equals(family, ClaudeFamily, StringComparison.OrdinalIgnoreCase);
            // Any OpenAI-signed ChatGPT or Codex package, including renamed or
            // preview editions that keep the product prefix.
            return family.EndsWith(OpenAIPublisher, StringComparison.OrdinalIgnoreCase) &&
                GptNames.Any(name => family.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool OwnsProcess(string app, uint pid)
        {
            return Owns(app, Native.PackageOf(pid));
        }

        // Every packaged app that has run for this user has a data folder here.
        private static bool Installed(string family)
        {
            return Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family));
        }

        // The shell entry to start. Explorer opens an unrelated folder window
        // for an unknown entry, so only one candidate is ever tried.
        internal static string AppId(string app)
        {
            if (app == "Claude") return ClaudeFamily + "!Claude";
            var entry = Gpt.FirstOrDefault(row => Installed(row[0])) ?? Gpt[0];
            return entry[0] + "!" + entry[1];
        }

        internal static string[] Aliases(string app)
        {
            return app == "Claude" ? new[] { "claude-desktop.exe" } : new[] { "chatgpt-desktop.exe", "chatgpt.exe" };
        }
    }
}
