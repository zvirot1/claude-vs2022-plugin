using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Diff
{
    /// <summary>
    /// Creates file snapshots before Claude modifies them, enabling revert capability.
    /// Port of com.anthropic.claude.intellij.diff.CheckpointManager.
    /// </summary>
    public class CheckpointManager
    {
        private readonly Dictionary<string, string> _snapshots = new Dictionary<string, string>();
        private readonly object _lock = new object();

        /// <summary>
        /// Take a one-time snapshot of a file before Claude modifies it.
        /// Does NOT overwrite if a snapshot already exists (preserves original baseline).
        /// </summary>
        public void Snapshot(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            var normalized = Path.GetFullPath(filePath);

            lock (_lock)
            {
                if (_snapshots.ContainsKey(normalized)) return;
                try
                {
                    if (File.Exists(normalized))
                    {
                        _snapshots[normalized] = File.ReadAllText(normalized);
                    }
                }
                catch { }
            }
        }

        public bool CanRevert(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock) { return _snapshots.ContainsKey(normalized); }
        }

        public bool Revert(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                if (!_snapshots.TryGetValue(normalized, out var content)) return false;
                try
                {
                    File.WriteAllText(normalized, content);
                    _snapshots.Remove(normalized);
                    return true;
                }
                catch { return false; }
            }
        }

        public string? GetCheckpoint(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                return _snapshots.TryGetValue(normalized, out var content) ? content : null;
            }
        }

        public IReadOnlyDictionary<string, string> GetSnapshots()
        {
            lock (_lock) { return new Dictionary<string, string>(_snapshots); }
        }

        public void ClearCheckpoints()
        {
            lock (_lock) { _snapshots.Clear(); }
        }
    }
}
