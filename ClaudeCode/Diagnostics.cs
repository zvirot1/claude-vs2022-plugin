using System;
using System.IO;
using ClaudeCode.Settings;

namespace ClaudeCode
{
    /// <summary>
    /// Eclipse fix #8: global diagnostic logging gate.
    /// Logs only when DiagEnabled is set in settings OR env var CLAUDE_DIAG=1 is present.
    /// Avoids spamming the debug log during normal operation.
    /// </summary>
    public static class Diagnostics
    {
        public static bool Enabled
        {
            get
            {
                try
                {
                    if (Environment.GetEnvironmentVariable("CLAUDE_DIAG") == "1") return true;
                }
                catch { }
                try { return ClaudeSettings.Instance.DiagEnabled; }
                catch { return false; }
            }
        }

        public static void Log(string tag, string message)
        {
            if (!Enabled) return;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCode");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "diag.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}\n");
            }
            catch { }
        }
    }
}
