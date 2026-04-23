namespace ClaudeCode.Cli
{
    /// <summary>
    /// Configuration for launching a Claude CLI process.
    /// Port of com.anthropic.claude.intellij.cli.CliProcessConfig.
    /// </summary>
    public sealed class CliProcessConfig
    {
        public string CliPath { get; }
        public string WorkingDirectory { get; }
        public string? PermissionMode { get; set; }
        public string? Model { get; set; }
        public string? SessionId { get; set; }
        public bool ContinueSession { get; set; }
        public string? ResumeSessionId { get; set; }
        public string[]? AllowedTools { get; set; }
        public string? AppendSystemPrompt { get; set; }
        public int MaxTurns { get; set; }
        public string[]? AdditionalDirs { get; set; }
        /// <summary>
        /// Effort/thinking budget level. null or "auto" = no flag (CLI default).
        /// Valid values: low, medium, high, max. Port from Eclipse plugin.
        /// </summary>
        public string? Effort { get; set; }

        public CliProcessConfig(string cliPath, string workingDirectory)
        {
            CliPath = cliPath;
            WorkingDirectory = workingDirectory;
        }

        private CliProcessConfig CloneWith(System.Action<CliProcessConfig> mutate)
        {
            var clone = new CliProcessConfig(CliPath, WorkingDirectory)
            {
                PermissionMode = this.PermissionMode,
                Model = this.Model,
                SessionId = this.SessionId,
                ContinueSession = this.ContinueSession,
                ResumeSessionId = this.ResumeSessionId,
                AllowedTools = this.AllowedTools,
                AppendSystemPrompt = this.AppendSystemPrompt,
                MaxTurns = this.MaxTurns,
                AdditionalDirs = this.AdditionalDirs,
                Effort = this.Effort,
            };
            mutate(clone);
            return clone;
        }

        /// <summary>
        /// Returns a copy of this config with the specified resume-session-id set.
        /// Used for session preservation on Stop (port from Eclipse Phase 5).
        /// </summary>
        public CliProcessConfig WithResume(string resumeSessionId)
            => CloneWith(c => c.ResumeSessionId = resumeSessionId);

        /// <summary>
        /// Returns a copy of this config with the specified permission mode set.
        /// Used for runtime permission mode switching (Milestone 3).
        /// </summary>
        public CliProcessConfig WithPermissionMode(string? permissionMode)
            => CloneWith(c => c.PermissionMode = permissionMode);

        /// <summary>
        /// Returns a copy of this config with the specified effort level set.
        /// Pass null or "auto" to clear (use CLI default).
        /// Used for runtime effort level switching (Round 3).
        /// </summary>
        public CliProcessConfig WithEffort(string? effort)
            => CloneWith(c => c.Effort = (effort == "auto") ? null : effort);
    }
}
