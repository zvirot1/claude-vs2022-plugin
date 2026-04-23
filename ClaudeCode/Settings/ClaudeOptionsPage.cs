using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.Settings
{
    /// <summary>
    /// VS Options page for Claude Code settings (Tools > Options > Claude Code).
    /// </summary>
    [Guid("E3F4A5B6-C7D8-9012-EF01-345678901234")]
    public class ClaudeOptionsPage : DialogPage
    {
        [Category("General")]
        [DisplayName("CLI Path")]
        [Description("Path to claude.exe or claude.cmd. Leave empty for auto-detect. Changes take effect after restart.")]
        public string CliPath { get; set; } = "";

        [Category("General")]
        [DisplayName("Model")]
        [Description("Model identifier: default, sonnet, opus.")]
        public string SelectedModel { get; set; } = "default";

        [Category("General")]
        [DisplayName("Permission Mode")]
        [Description("Initial permission mode: default, plan, acceptEdits, bypassPermissions.")]
        public string InitialPermissionMode { get; set; } = "default";

        [Category("Behavior")]
        [DisplayName("Auto-save after edits")]
        [Description("Whether to auto-save files after Claude edits them.")]
        public bool Autosave { get; set; } = true;

        [Category("Behavior")]
        [DisplayName("Use Ctrl+Enter to send")]
        [Description("When true, Ctrl+Enter sends the message instead of plain Enter.")]
        public bool UseCtrlEnterToSend { get; set; } = false;

        [Category("Behavior")]
        [DisplayName("Max Tokens")]
        [Description("Maximum tokens for CLI responses (0 = CLI default).")]
        public int MaxTokens { get; set; } = 0;

        [Category("Behavior")]
        [DisplayName("System Prompt")]
        [Description("Custom system prompt appended to the default. Empty = none.")]
        public string SystemPrompt { get; set; } = "";

        [Category("Display")]
        [DisplayName("Show Cost")]
        [Description("Whether to show cost display in the status bar.")]
        public bool ShowCost { get; set; } = true;

        [Category("Display")]
        [DisplayName("Show Streaming")]
        [Description("Whether to show streaming output in real time.")]
        public bool ShowStreaming { get; set; } = true;

        [Category("Authentication")]
        [DisplayName("API Key")]
        [Description("Anthropic API key. Leave empty to use OAuth or environment variable.")]
        [PasswordPropertyText(true)]
        public string ApiKey { get; set; } = "";

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            // Sync to our settings singleton
            var settings = ClaudeSettings.Instance;
            settings.CliPath = CliPath;
            settings.SelectedModel = SelectedModel;
            settings.InitialPermissionMode = InitialPermissionMode;
            settings.Autosave = Autosave;
            settings.UseCtrlEnterToSend = UseCtrlEnterToSend;
            settings.MaxTokens = MaxTokens;
            settings.SystemPrompt = SystemPrompt;
            settings.ShowCost = ShowCost;
            settings.ShowStreaming = ShowStreaming;
            settings.ApiKey = ApiKey;
            settings.Save();

            // Also update claude_cli_path.txt in the extension directory
            // so GetCliPath can find it (VS sandbox prevents File.Exists on external paths)
            if (!string.IsNullOrEmpty(CliPath))
            {
                UpdateCliPathFile(CliPath);
            }
        }

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            var settings = ClaudeSettings.Instance;
            CliPath = settings.CliPath;
            SelectedModel = settings.SelectedModel;
            InitialPermissionMode = settings.InitialPermissionMode;
            Autosave = settings.Autosave;
            UseCtrlEnterToSend = settings.UseCtrlEnterToSend;
            MaxTokens = settings.MaxTokens;
            SystemPrompt = settings.SystemPrompt;
            ShowCost = settings.ShowCost;
            ShowStreaming = settings.ShowStreaming;
            ApiKey = settings.ApiKey;

            // If CliPath is empty, show current resolved path from the path file
            if (string.IsNullOrEmpty(CliPath))
            {
                CliPath = ReadCurrentCliPath();
            }
        }

        private static void UpdateCliPathFile(string cliPath)
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                var pathFile = Path.Combine(assemblyDir, "claude_cli_path.txt");
                File.WriteAllText(pathFile, cliPath);
            }
            catch { }
        }

        private static string ReadCurrentCliPath()
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                var pathFile = Path.Combine(assemblyDir, "claude_cli_path.txt");
                if (File.Exists(pathFile))
                {
                    var path = File.ReadAllText(pathFile).Trim();
                    // Resolve relative to assembly dir
                    if (!Path.IsPathRooted(path))
                        path = Path.Combine(assemblyDir, path);
                    return path;
                }
            }
            catch { }
            return "";
        }
    }
}
