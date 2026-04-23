using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeCode.Diff
{
    /// <summary>
    /// Manages accept/reject workflow for Claude's file edits.
    /// Port of com.anthropic.claude.intellij.diff.EditDecisionManager.
    /// </summary>
    public class EditDecisionManager
    {
        public enum EditState { Pending, Accepted, Rejected }

        public class PendingEdit
        {
            public int Id { get; }
            public string FilePath { get; }
            public string? OriginalContent { get; set; }
            public string? ModifiedContent { get; set; }
            public EditState State { get; set; } = EditState.Pending;

            public PendingEdit(int id, string filePath)
            {
                Id = id;
                FilePath = filePath;
            }
        }

        public interface IEditDecisionListener
        {
            void OnEditStateChanged(PendingEdit edit);
        }

        private readonly Dictionary<string, PendingEdit> _edits = new Dictionary<string, PendingEdit>();
        private readonly CheckpointManager _checkpointManager;
        private readonly List<IEditDecisionListener> _listeners = new List<IEditDecisionListener>();
        private readonly object _lock = new object();
        private int _editCounter;

        public EditDecisionManager(CheckpointManager checkpointManager)
        {
            _checkpointManager = checkpointManager;
        }

        public void AddListener(IEditDecisionListener listener) { lock (_lock) _listeners.Add(listener); }
        public void RemoveListener(IEditDecisionListener listener) { lock (_lock) _listeners.Remove(listener); }

        /// <summary>
        /// Record a completed edit for accept/reject review.
        /// </summary>
        public PendingEdit RecordCompletedEdit(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                var edit = new PendingEdit(++_editCounter, normalized)
                {
                    OriginalContent = _checkpointManager.GetCheckpoint(normalized),
                    ModifiedContent = File.Exists(normalized) ? File.ReadAllText(normalized) : null
                };
                _edits[normalized] = edit;
                return edit;
            }
        }

        /// <summary>
        /// Record a completed edit with known original content.
        /// </summary>
        public PendingEdit RecordCompletedEditWithOriginal(string filePath, string originalContent)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                var edit = new PendingEdit(++_editCounter, normalized)
                {
                    OriginalContent = originalContent,
                    ModifiedContent = File.Exists(normalized) ? File.ReadAllText(normalized) : null
                };
                _edits[normalized] = edit;
                return edit;
            }
        }

        public void AcceptEdit(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                if (_edits.TryGetValue(normalized, out var edit))
                {
                    edit.State = EditState.Accepted;
                    NotifyListeners(edit);
                }
            }
        }

        public void RejectEdit(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                if (_edits.TryGetValue(normalized, out var edit) && edit.OriginalContent != null)
                {
                    try
                    {
                        File.WriteAllText(normalized, edit.OriginalContent);
                        edit.State = EditState.Rejected;
                        NotifyListeners(edit);
                    }
                    catch { }
                }
            }
        }

        public void AcceptAll()
        {
            lock (_lock)
            {
                foreach (var edit in _edits.Values.Where(e => e.State == EditState.Pending))
                {
                    edit.State = EditState.Accepted;
                    NotifyListeners(edit);
                }
            }
        }

        public void RejectAll()
        {
            lock (_lock)
            {
                foreach (var edit in _edits.Values.Where(e => e.State == EditState.Pending).ToList())
                {
                    if (edit.OriginalContent != null)
                    {
                        try
                        {
                            File.WriteAllText(edit.FilePath, edit.OriginalContent);
                            edit.State = EditState.Rejected;
                            NotifyListeners(edit);
                        }
                        catch { }
                    }
                }
            }
        }

        public int GetPendingCount()
        {
            lock (_lock) { return _edits.Values.Count(e => e.State == EditState.Pending); }
        }

        public List<PendingEdit> GetEditsForFile(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);
            lock (_lock)
            {
                return _edits.Values.Where(e => e.FilePath == normalized).ToList();
            }
        }

        /// <summary>Look up edit by integer ID OR by file path string.</summary>
        public PendingEdit? GetEdit(string idOrPath)
        {
            lock (_lock)
            {
                if (int.TryParse(idOrPath, out var id))
                {
                    return _edits.Values.FirstOrDefault(e => e.Id == id);
                }
                try
                {
                    var normalized = Path.GetFullPath(idOrPath);
                    return _edits.TryGetValue(normalized, out var byPath) ? byPath : null;
                }
                catch { return null; }
            }
        }

        public void Clear()
        {
            lock (_lock) { _edits.Clear(); }
        }

        private void NotifyListeners(PendingEdit edit)
        {
            List<IEditDecisionListener> snapshot;
            lock (_lock) { snapshot = new List<IEditDecisionListener>(_listeners); }
            foreach (var l in snapshot)
            {
                try { l.OnEditStateChanged(edit); } catch { }
            }
        }
    }
}
