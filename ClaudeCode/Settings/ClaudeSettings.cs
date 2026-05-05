using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace ClaudeCode.Settings
{
    /// <summary>
    /// Persistent settings for the Claude Code extension.
    /// Stored in %LOCALAPPDATA%\ClaudeCode\settings.json.
    /// Port of com.anthropic.claude.intellij.settings.ClaudeSettings.
    /// </summary>
    public class ClaudeSettings
    {
        private static ClaudeSettings? _instance;
        private static readonly object _lock = new();
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCode", "settings.json");

        public string CliPath { get; set; } = "";
        public string SelectedModel { get; set; } = "default";
        public string InitialPermissionMode { get; set; } = "default";
        public bool Autosave { get; set; } = true;
        public bool UseCtrlEnterToSend { get; set; } = false;
        public bool RespectGitIgnore { get; set; } = true;
        public int MaxTokens { get; set; } = 0;
        public string SystemPrompt { get; set; } = "";
        public bool ShowCost { get; set; } = true;
        public bool ShowStreaming { get; set; } = true;
        public int SessionHistoryLimit { get; set; } = 100;
        public string ApiKey { get; set; } = "";
        public string LastKnownCliPath { get; set; } = "";
        public bool AutoSaveBeforeTools { get; set; } = false;

        /// <summary>
        /// Effort/thinking budget level. Values: auto (default - no flag), low, medium, high, max.
        /// Port from Eclipse plugin. Hot-swapped via CLI restart with --resume.
        /// </summary>
        public string EffortLevel { get; set; } = "auto";

        /// <summary>B1: Last session id per tool-window instance — for auto-resume on restart.</summary>
        public Dictionary<int, string> LastSessionIdPerInstance { get; set; } = new Dictionary<int, string>();

        /// <summary>B3: Instance IDs that were open at last shutdown — restored on startup.</summary>
        public List<int> OpenInstanceIds { get; set; } = new List<int>();

        /// <summary>C1: User-added custom model names (shown in addition to presets).</summary>
        public List<string> CustomModels { get; set; } = new List<string>();

        /// <summary>Eclipse fix #8: enable verbose diagnostic logging (off by default to avoid noise).
        /// Can also be toggled via env var CLAUDE_DIAG=1 without changing this setting.</summary>
        public bool DiagEnabled { get; set; } = false;

        /// <summary>IntelliJ Round 7 / Amazon Q parity: when ON, the file currently open
        /// in the code editor is auto-attached as `&lt;file path="..."&gt;...&lt;/file&gt;` context
        /// to every outgoing message. Off by default to avoid surprising token costs.</summary>
        public bool AttachActiveFile { get; set; } = false;

        /// <summary>Eclipse Round 8: configurable user-skills folder for the SkillsDialog.
        /// Empty string = use default (~/.claude/skills/, matching CLI + IntelliJ convention).</summary>
        public string SkillsFolder { get; set; } = "";

        public static ClaudeSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= Load();
                    }
                }
                return _instance;
            }
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            // Retry on IO contention (parallel CLI/panel writing)
            for (int i = 0; i < 5; i++)
            {
                try { File.WriteAllText(SettingsPath, json); return; }
                catch (IOException) { Thread.Sleep(50); }
                catch { return; }
            }
        }

        private static ClaudeSettings Load()
        {
            // Retry on IO contention (another process mid-write)
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!File.Exists(SettingsPath)) return new ClaudeSettings();
                    var json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<ClaudeSettings>(json) ?? new ClaudeSettings();
                }
                catch (IOException) { Thread.Sleep(50); }
                catch { return new ClaudeSettings(); }
            }
            return new ClaudeSettings();
        }
    }
}
