using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeCode.Handlers
{
    /// <summary>
    /// Handles slash commands typed in the conversation input.
    /// Port of com.anthropic.claude.intellij.handlers.SlashCommandHandler.
    /// </summary>
    public static class SlashCommandHandler
    {
        public class CommandInfo
        {
            public string Name { get; }
            public string Description { get; }
            public bool IsLocalOnly { get; }
            public bool HasSubOptions { get; }

            public CommandInfo(string name, string description, bool localOnly, bool hasSubOptions = false)
            {
                Name = name;
                Description = description;
                IsLocalOnly = localOnly;
                HasSubOptions = hasSubOptions;
            }
        }

        public class SubOption
        {
            public string Value { get; }
            public string Label { get; }
            public string Description { get; }

            public SubOption(string value, string label, string description)
            {
                Value = value;
                Label = label;
                Description = description;
            }
        }

        private static readonly List<CommandInfo> Commands = new List<CommandInfo>
        {
            // Local commands
            new CommandInfo("/new", "Start a new conversation", true),
            new CommandInfo("/clear", "Clear the conversation display", true),
            new CommandInfo("/cost", "Show token usage and cost summary", true),
            new CommandInfo("/help", "Show available commands", true),
            new CommandInfo("/stop", "Stop the current query", true),
            new CommandInfo("/model", "Switch to a different model", true, true),
            new CommandInfo("/resume", "Resume a previous session", true),
            new CommandInfo("/history", "Browse and search session history", true),
            new CommandInfo("/compact", "Compact conversation context", true),
            new CommandInfo("/rules", "Manage Claude Code rules", true),
            new CommandInfo("/mcp", "Manage MCP servers", true),
            new CommandInfo("/hooks", "Manage hooks", true),
            new CommandInfo("/memory", "Edit project memory", true),
            new CommandInfo("/skills", "Browse installed plugins and skills", true),
            // CLI-forwarded commands
            new CommandInfo("/commit", "Generate a git commit message", false),
            new CommandInfo("/review-pr", "Review a pull request", false),
            new CommandInfo("/explain", "Explain the current file or selection", false),
            new CommandInfo("/fix", "Fix bugs in the current file", false),
            new CommandInfo("/test", "Generate tests for the current code", false),
            new CommandInfo("/refactor", "Refactor the current code", false)
        };

        public static bool IsSlashCommand(string input)
            => !string.IsNullOrEmpty(input) && input.StartsWith("/") && !input.StartsWith("//");

        public static bool IsLocalCommand(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            var cmd = input.Split(' ')[0].ToLower();
            return Commands.Any(c => c.Name == cmd && c.IsLocalOnly);
        }

        public static string GetCommandName(string input)
            => string.IsNullOrEmpty(input) ? "" : input.Split(' ')[0].ToLower();

        public static string GetCommandArgs(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var trimmed = input.Trim();
            var spaceIdx = trimmed.IndexOf(' ');
            return spaceIdx < 0 ? "" : trimmed.Substring(spaceIdx + 1).Trim();
        }

        public static IReadOnlyList<CommandInfo> GetAllCommands() => Commands;

        public static List<CommandInfo> GetSuggestions(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return new List<CommandInfo>();
            var lower = prefix.ToLower();
            return Commands.Where(c => c.Name.StartsWith(lower)).ToList();
        }

        public static List<SubOption> GetSubOptions(string commandName)
        {
            if (commandName == "/model")
            {
                return new List<SubOption>
                {
                    new SubOption("sonnet", "Sonnet", "Claude Sonnet - fast and capable"),
                    new SubOption("opus", "Opus", "Claude Opus - most powerful"),
                    new SubOption("haiku", "Haiku", "Claude Haiku - fastest and lightest"),
                    new SubOption("claude-sonnet-4-20250514", "Sonnet 4", "Claude Sonnet 4 specific version"),
                    new SubOption("claude-opus-4-20250514", "Opus 4", "Claude Opus 4 specific version")
                };
            }
            return new List<SubOption>();
        }

        public static string FormatHelp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Available Commands\n");
            sb.AppendLine("**Plugin Commands:**\n");
            foreach (var cmd in Commands.Where(c => c.IsLocalOnly))
                sb.AppendLine($"- `{cmd.Name}` - {cmd.Description}");
            sb.AppendLine("\n**Claude Commands** (forwarded to CLI):\n");
            foreach (var cmd in Commands.Where(c => !c.IsLocalOnly))
                sb.AppendLine($"- `{cmd.Name}` - {cmd.Description}");
            return sb.ToString();
        }

        public static string FormatCost(string tokens, string cost, string duration, int turns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Session Cost Summary\n");
            if (!string.IsNullOrEmpty(tokens)) sb.AppendLine($"- **Tokens:** {tokens}");
            if (!string.IsNullOrEmpty(cost)) sb.AppendLine($"- **Cost:** {cost}");
            if (!string.IsNullOrEmpty(duration)) sb.AppendLine($"- **Duration:** {duration}");
            if (turns > 0) sb.AppendLine($"- **Turns:** {turns}");
            return sb.ToString();
        }
    }
}
