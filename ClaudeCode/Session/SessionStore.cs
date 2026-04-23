using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCode.Model;
using Newtonsoft.Json;

namespace ClaudeCode.Session
{
    /// <summary>
    /// File-based persistence layer for session metadata.
    /// Stores sessions as JSON files in ~/.claude/claude-sessions/.
    /// Port of com.anthropic.claude.intellij.session.SessionStore.
    /// </summary>
    public class SessionStore
    {
        private readonly string _sessionsDir;

        public SessionStore()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _sessionsDir = Path.Combine(home, ".claude", "claude-sessions");
            try
            {
                if (!Directory.Exists(_sessionsDir))
                    Directory.CreateDirectory(_sessionsDir);
            }
            catch
            {
                _sessionsDir = Path.Combine(Path.GetTempPath(), "claude-sessions");
                if (!Directory.Exists(_sessionsDir))
                    Directory.CreateDirectory(_sessionsDir);
            }
        }

        public void SaveSession(SessionInfo info)
        {
            if (info?.SessionId == null) return;
            try
            {
                var path = Path.Combine(_sessionsDir, info.SessionId + ".json");
                var json = JsonConvert.SerializeObject(info, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public SessionInfo? LoadSession(string sessionId)
        {
            try
            {
                var path = Path.Combine(_sessionsDir, sessionId + ".json");
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SessionInfo>(json);
            }
            catch { return null; }
        }

        public List<SessionInfo> ListSessions()
        {
            var sessions = new List<SessionInfo>();
            try
            {
                foreach (var file in Directory.GetFiles(_sessionsDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var info = JsonConvert.DeserializeObject<SessionInfo>(json);
                        if (info != null) sessions.Add(info);
                    }
                    catch { }
                }
            }
            catch { }
            return sessions.OrderByDescending(s => s.LastActiveTime).ToList();
        }

        public void DeleteSession(string sessionId)
        {
            try
            {
                var path = Path.Combine(_sessionsDir, sessionId + ".json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public void Cleanup(int keepCount = 50)
        {
            try
            {
                var sessions = ListSessions();
                if (sessions.Count > keepCount)
                {
                    foreach (var session in sessions.Skip(keepCount))
                    {
                        if (session.SessionId != null)
                            DeleteSession(session.SessionId);
                    }
                }
            }
            catch { }
        }
    }
}
