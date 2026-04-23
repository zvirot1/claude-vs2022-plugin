namespace ClaudeCode.Model
{
    /// <summary>
    /// Tracks cumulative token usage, cost, and duration for a conversation session.
    /// Port of com.anthropic.claude.intellij.model.UsageInfo.
    /// </summary>
    public class UsageInfo
    {
        public int TotalInputTokens { get; private set; }
        public int TotalOutputTokens { get; private set; }
        public int TotalTokens => TotalInputTokens + TotalOutputTokens;
        public double TotalCostUsd { get; private set; }
        public long TotalDurationMs { get; private set; }
        public int TotalTurns { get; private set; }

        public void AddUsage(int inputTokens, int outputTokens, double costUsd, long durationMs, int turns)
        {
            TotalInputTokens += inputTokens;
            TotalOutputTokens += outputTokens;
            TotalCostUsd += costUsd;
            TotalDurationMs += durationMs;
            TotalTurns += turns;
        }

        public void Reset()
        {
            TotalInputTokens = 0;
            TotalOutputTokens = 0;
            TotalCostUsd = 0;
            TotalDurationMs = 0;
            TotalTurns = 0;
        }

        public string FormatCost() =>
            TotalCostUsd < 0.01 ? $"${TotalCostUsd:F4}" : $"${TotalCostUsd:F2}";

        public string FormatTokens() =>
            $"{TotalInputTokens:N0} in / {TotalOutputTokens:N0} out";

        public string FormatDuration()
        {
            if (TotalDurationMs < 1000) return $"{TotalDurationMs}ms";
            var seconds = TotalDurationMs / 1000.0;
            if (seconds < 60) return $"{seconds:F1}s";
            var minutes = (int)(seconds / 60);
            var secs = (int)(seconds % 60);
            return $"{minutes}m {secs}s";
        }

        public override string ToString() =>
            $"Tokens: {FormatTokens()} | Cost: {FormatCost()} | Duration: {FormatDuration()} | Turns: {TotalTurns}";
    }
}
