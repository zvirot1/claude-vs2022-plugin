using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCode.Model
{
    /// <summary>
    /// Represents a single conversation turn (message) in the conversation.
    /// Port of com.anthropic.claude.intellij.model.MessageBlock.
    /// </summary>
    public class MessageBlock
    {
        public enum Role { User, Assistant, System, Error }
        public enum ToolStatus { Running, Completed, Failed, NeedsPermission }

        public Role MessageRole { get; }
        public long Timestamp { get; }
        public List<ContentSegment> Segments { get; } = new();

        public MessageBlock(Role role)
        {
            MessageRole = role;
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public void AddSegment(ContentSegment segment) => Segments.Add(segment);

        public TextSegment GetOrCreateLastTextSegment()
        {
            if (Segments.Count > 0 && Segments[Segments.Count - 1] is TextSegment existing)
                return existing;
            var seg = new TextSegment();
            Segments.Add(seg);
            return seg;
        }

        public string GetFullText()
        {
            var sb = new StringBuilder();
            foreach (var seg in Segments)
            {
                if (seg is TextSegment ts)
                    sb.Append(ts.Text);
            }
            return sb.ToString();
        }

        public ToolCallSegment? FindToolCall(string toolId)
        {
            foreach (var seg in Segments)
            {
                if (seg is ToolCallSegment tc && tc.ToolId == toolId)
                    return tc;
            }
            return null;
        }

        #region Content Segment Types

        public abstract class ContentSegment
        {
            public abstract string SegmentType { get; }
        }

        public class TextSegment : ContentSegment
        {
            private readonly StringBuilder _text = new();
            public override string SegmentType => "text";
            public string Text => _text.ToString();
            public int Length => _text.Length;
            public void AppendText(string delta) => _text.Append(delta);
        }

        public class ToolCallSegment : ContentSegment
        {
            private readonly StringBuilder _inputBuilder = new();
            public override string SegmentType => "tool_use";
            public string? ToolId { get; set; }
            public string? ToolName { get; set; }
            public string? Input { get; set; }

            // Port from Eclipse Phase 6: volatile on Status and Output
            // prevents stale Running status from overwriting Completed in async handlers.
            private volatile int _statusInt = (int)ToolStatus.Running;
            public ToolStatus Status
            {
                get => (ToolStatus)_statusInt;
                set => _statusInt = (int)value;
            }

            private volatile string? _output;
            public string? Output
            {
                get => _output;
                set => _output = value;
            }

            public void AppendInput(string delta)
            {
                _inputBuilder.Append(delta);
                Input = _inputBuilder.ToString();
            }

            public string GetDisplayName()
            {
                return ToolName switch
                {
                    "Read" => "Read File",
                    "Edit" => "Edit File",
                    "Write" => "Write File",
                    "Bash" => "Run Command",
                    "Grep" => "Search Code",
                    "Glob" => "Find Files",
                    "WebSearch" => "Web Search",
                    "WebFetch" => "Fetch URL",
                    "Task" => "Sub-Agent",
                    "TodoWrite" => "Update Tasks",
                    _ => ToolName ?? "Unknown Tool"
                };
            }

            public string GetSummary()
            {
                if (string.IsNullOrEmpty(Input)) return GetDisplayName();
                try
                {
                    if (ToolName is "Read" or "Write" or "Edit")
                    {
                        var path = ExtractJsonStringValue(Input!, "file_path");
                        if (path != null)
                        {
                            var lastSlash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
                            return GetDisplayName() + ": " + (lastSlash >= 0 ? path.Substring(lastSlash + 1) : path);
                        }
                    }
                    else if (ToolName == "Bash")
                    {
                        var cmd = ExtractJsonStringValue(Input!, "command");
                        if (cmd != null)
                            return GetDisplayName() + ": " + (cmd.Length > 60 ? cmd.Substring(0, 57) + "..." : cmd);
                    }
                    else if (ToolName == "Grep")
                    {
                        var pattern = ExtractJsonStringValue(Input!, "pattern");
                        if (pattern != null)
                            return GetDisplayName() + ": " + pattern;
                    }
                }
                catch { }
                return GetDisplayName();
            }

            private static string? ExtractJsonStringValue(string json, string key)
            {
                var marker = $"\"{key}\":\"";
                var start = json.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0)
                {
                    marker = $"\"{key}\": \"";
                    start = json.IndexOf(marker, StringComparison.Ordinal);
                }
                if (start < 0) return null;
                start += marker.Length;
                var end = json.IndexOf('"', start);
                if (end < 0) return null;
                return json.Substring(start, end - start).Replace("\\n", "\n").Replace("\\\"", "\"");
            }
        }

        public class ToolResultSegment : ContentSegment
        {
            public override string SegmentType => "tool_result";
            public string? ToolUseId { get; set; }
            public string? Content { get; set; }
            public bool IsError { get; set; }
        }

        #endregion
    }
}
