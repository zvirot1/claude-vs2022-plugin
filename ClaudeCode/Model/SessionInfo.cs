using System;

namespace ClaudeCode.Model
{
    /// <summary>
    /// Session metadata for tracking and resuming Claude conversations.
    /// Port of com.anthropic.claude.intellij.model.SessionInfo.
    /// </summary>
    public class SessionInfo
    {
        public string? SessionId { get; set; }
        public string? Model { get; set; }
        public string? WorkingDirectory { get; set; }
        public long StartTime { get; set; }
        public long LastActiveTime { get; set; }
        public string? PermissionMode { get; set; }
        public int MessageCount { get; set; }
        public string? Summary { get; set; }

        public SessionInfo()
        {
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastActiveTime = StartTime;
        }

        public SessionInfo(string sessionId) : this()
        {
            SessionId = sessionId;
        }

        public void Touch() => LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public string GetDisplayLabel()
        {
            var desc = !string.IsNullOrEmpty(Summary) ? Summary!
                : SessionId != null && SessionId.Length > 8 ? $"Session {SessionId.Substring(0, 8)}..."
                : $"Session {SessionId ?? "unknown"}";

            if (LastActiveTime > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(LastActiveTime).LocalDateTime;
                return $"[{dt:dd/MM/yy HH:mm}]  {desc}";
            }
            return desc;
        }
    }
}
