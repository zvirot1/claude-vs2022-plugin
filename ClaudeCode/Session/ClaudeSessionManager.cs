using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCode.Model;

namespace ClaudeCode.Session
{
    /// <summary>
    /// Manages conversation session lifecycle - creating, resuming, persisting.
    /// Port of com.anthropic.claude.intellij.session.ClaudeSessionManager.
    /// </summary>
    public class ClaudeSessionManager
    {
        private readonly SessionStore _store = new SessionStore();
        private SessionInfo? _currentSession;

        public SessionInfo? CurrentSession => _currentSession;

        public SessionInfo StartNewSession(string? workingDirectory)
        {
            _currentSession = new SessionInfo(Guid.NewGuid().ToString())
            {
                WorkingDirectory = workingDirectory
            };
            return _currentSession;
        }

        public SessionInfo? ResumeSession(string sessionId)
        {
            var info = _store.LoadSession(sessionId);
            if (info != null)
                _currentSession = info;
            return info;
        }

        /// <summary>
        /// Adopt whatever sessionId the CLI emitted in SystemInit as the current session,
        /// loading existing metadata from disk if any. Guarantees <see cref="CurrentSession"/>
        /// is non-null so later calls to <see cref="SaveCurrentSession"/> and
        /// <see cref="RenameSession"/> actually persist.
        /// </summary>
        public SessionInfo TrackSession(string sessionId, string? workingDirectory)
        {
            var existed = _store.LoadSession(sessionId);
            var info = existed ?? new SessionInfo(sessionId)
            {
                WorkingDirectory = workingDirectory
            };
            if (string.IsNullOrEmpty(info.WorkingDirectory) && !string.IsNullOrEmpty(workingDirectory))
                info.WorkingDirectory = workingDirectory;
            info.Touch();
            _currentSession = info;
            // Persist immediately so the session shows in Session History even before
            // the first turn completes and SaveCurrentSession runs.
            _store.SaveSession(info);
            return info;
        }

        public void SaveCurrentSession(ConversationModel model)
        {
            if (_currentSession == null) return;

            // Update session from model state
            var modelSession = model.SessionInfo;
            if (modelSession != null)
            {
                _currentSession.SessionId = modelSession.SessionId ?? _currentSession.SessionId;
                _currentSession.Model = modelSession.Model;
                _currentSession.WorkingDirectory = modelSession.WorkingDirectory ?? _currentSession.WorkingDirectory;
                _currentSession.PermissionMode = modelSession.PermissionMode;
            }

            _currentSession.MessageCount = model.MessageCount;
            _currentSession.Touch();

            // Auto-generate summary from first user message — but only if neither the
            // user nor the CLI has provided one. IntelliJ port (b97fbe6 + 3233fd4):
            //  1) prefer a CLI-emitted summary if the JSONL contains one.
            //  2) otherwise use the cleaned first user prompt (strip file-XML / active-editor
            //     context noise so we don't show '<file path="...">...' as the title).
            if (string.IsNullOrEmpty(_currentSession.Summary))
            {
                var cliSummary = TryReadCliSummaryFromJsonl(_currentSession.SessionId, _currentSession.WorkingDirectory);
                if (!string.IsNullOrEmpty(cliSummary))
                {
                    _currentSession.Summary = cliSummary;
                }
                else
                {
                    var messages = model.GetMessages();
                    foreach (var msg in messages)
                    {
                        if (msg.MessageRole == MessageBlock.Role.User)
                        {
                            var text = SessionJsonlLoader.StripPrependedNoise(msg.GetFullText().Trim());
                            if (!string.IsNullOrEmpty(text))
                            {
                                _currentSession.Summary = text.Length > 60
                                    ? text.Substring(0, 57) + "..."
                                    : text;
                                break;
                            }
                        }
                    }
                }
            }

            _store.SaveSession(_currentSession);
        }

        /// <summary>Reads the last <c>{"type":"summary","summary":"..."}</c> entry from the
        /// session's JSONL transcript. The CLI itself writes these — terse LLM-authored
        /// titles that match the VS Code / CLI UX. Returns null if not found.</summary>
        private static string? TryReadCliSummaryFromJsonl(string? sessionId, string? workingDir)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(workingDir)) return null;
                var encoded = workingDir!.Replace('\\', '-').Replace('/', '-').Replace(':', '-');
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var path = System.IO.Path.Combine(home, ".claude", "projects", encoded, sessionId + ".jsonl");
                if (!System.IO.File.Exists(path)) return null;
                string? best = null;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"summary\"")) continue;
                    try
                    {
                        var obj = Newtonsoft.Json.Linq.JObject.Parse(line);
                        if (obj.Value<string>("type") != "summary") continue;
                        var s = obj.Value<string>("summary");
                        if (!string.IsNullOrEmpty(s)) best = s;   // last-one-wins
                    }
                    catch { }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// IntelliJ port: list real CLI sessions by scanning the JSONL transcripts under
        /// <c>~/.claude/projects/</c>, then overlay any user-renamed Summary from our local
        /// plugin store. This avoids the plugin-store noise (one ghost entry per opened panel).
        /// </summary>
        public List<SessionInfo> ListSessions(string? projectDir = null)
        {
            var fromJsonl = JsonlSessionScanner.ListSessions(projectDir);
            // Overlay user renames from the plugin store (only the Summary field) so manual
            // renames survive even though the JSONL doesn't carry them.
            var renamed = _store.ListSessions()
                .Where(s => !string.IsNullOrEmpty(s.SessionId) && !string.IsNullOrEmpty(s.Summary))
                .ToDictionary(s => s.SessionId!, s => s.Summary!, StringComparer.OrdinalIgnoreCase);
            foreach (var info in fromJsonl)
            {
                if (info.SessionId != null && renamed.TryGetValue(info.SessionId, out var custom))
                    info.Summary = custom;
            }
            return fromJsonl;
        }

        public void DeleteSession(string sessionId) => _store.DeleteSession(sessionId);

        public void Cleanup() => _store.Cleanup(Settings.ClaudeSettings.Instance.SessionHistoryLimit);

        /// <summary>
        /// Resume the most recent session (sorted by lastActiveTime). Returns null if none.
        /// </summary>
        public SessionInfo? ContinueLastSession()
        {
            var sessions = ListSessions();
            if (sessions.Count == 0) return null;
            var first = sessions[0];
            return first.SessionId != null ? ResumeSession(first.SessionId) : null;
        }

        /// <summary>
        /// Update the user-friendly name (Summary) of a session and persist it.
        /// Returns true on success.
        /// </summary>
        public bool RenameSession(string sessionId, string newName)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            var info = _store.LoadSession(sessionId);
            if (info == null)
            {
                // Fix #4: sessions that were resumed via --resume may not be in our store yet.
                // Create a minimal record so the rename persists instead of silently failing.
                info = new SessionInfo(sessionId)
                {
                    Summary = newName,
                    MessageCount = _currentSession?.MessageCount ?? 0,
                    Model = _currentSession?.Model
                };
            }
            info.Summary = newName;
            _store.SaveSession(info);
            // If renaming the active session, update in-memory copy too
            if (_currentSession != null && _currentSession.SessionId == sessionId)
                _currentSession.Summary = newName;
            return true;
        }
    }
}
