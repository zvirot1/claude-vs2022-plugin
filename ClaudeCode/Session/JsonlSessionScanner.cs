using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCode.Model;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.Session
{
    /// <summary>
    /// Reads Claude CLI session transcripts directly from
    /// <c>~/.claude/projects/&lt;encoded-cwd&gt;/*.jsonl</c> instead of relying on the
    /// plugin's local SessionStore. Mirror of IntelliJ's <c>JsonlSessionScanner</c> —
    /// the JSONL files are the single source of truth, so the dialog shows real CLI
    /// sessions with real metadata (message counts, models, summaries) instead of
    /// the accumulated plugin-store ghosts.
    /// </summary>
    public static class JsonlSessionScanner
    {
        // Cap per file to avoid blocking the dialog on multi-GB transcripts.
        // First 500 lines reliably contain the first user message + early CLI summary.
        private const int MaxLinesPerFile = 500;

        // Strip leading <file path="...">...</file> blocks (file XML pin context).
        private static readonly Regex FileBlockRx = new(
            @"^(?:\s*<file\s+path=""[^""]*""\s*>[\s\S]*?</file>\s*)+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Strip leading [Active editor context: ...] markers (CLI-injected).
        private static readonly Regex ActiveCtxRx = new(
            @"^\s*\[Active editor context:[^\]]*\]\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Enumerate all CLI sessions, optionally filtered to a specific project directory.
        /// Newest first. Drops sessions with zero user/assistant messages.
        /// </summary>
        public static List<SessionInfo> ListSessions(string? projectDir = null)
        {
            var result = new List<SessionInfo>();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var projectsRoot = Path.Combine(home, ".claude", "projects");
            if (!Directory.Exists(projectsRoot)) return result;

            string? matchPrefixLower = null;
            if (!string.IsNullOrEmpty(projectDir))
                matchPrefixLower = EncodeProjectKey(projectDir!).ToLowerInvariant();

            string[] projectDirs;
            try { projectDirs = Directory.GetDirectories(projectsRoot); }
            catch { return result; }

            foreach (var dir in projectDirs)
            {
                if (matchPrefixLower != null)
                {
                    var name = Path.GetFileName(dir).ToLowerInvariant();
                    // Match either exact or with worktree suffix (e.g. "foo-bar")
                    if (name != matchPrefixLower && !name.StartsWith(matchPrefixLower + "-"))
                        continue;
                }

                string[] files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); }
                catch { continue; }

                foreach (var f in files)
                {
                    var sid = Path.GetFileNameWithoutExtension(f);
                    if (!IsUuid(sid)) continue;          // skip subagent-*.jsonl etc.
                    try
                    {
                        var info = BuildSessionInfo(f, sid, Path.GetFileName(dir));
                        if (info != null) result.Add(info);
                    }
                    catch { /* skip unreadable */ }
                }
            }

            // Newest first by LastActiveTime (file mtime)
            result.Sort((a, b) => b.LastActiveTime.CompareTo(a.LastActiveTime));
            return result;
        }

        private static SessionInfo? BuildSessionInfo(string jsonl, string sessionId, string projectKey)
        {
            var info = new SessionInfo(sessionId)
            {
                LastActiveTime = new DateTimeOffset(File.GetLastWriteTime(jsonl)).ToUnixTimeMilliseconds(),
                WorkingDirectory = projectKey
            };

            string? firstUserSummary = null;
            string? cliSummary = null;
            int messageCount = 0;
            long createdAt = 0;
            string? model = null;

            try
            {
                using var sr = new StreamReader(jsonl);
                string? line;
                int linesRead = 0;
                while ((line = sr.ReadLine()) != null && linesRead++ < MaxLinesPerFile)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    // Cheap pre-filter — only user/assistant/summary entries matter.
                    if (line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) < 0
                        && line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0
                        && line.IndexOf("\"type\":\"summary\"", StringComparison.Ordinal) < 0
                        && line.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) < 0)
                        continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    var type = obj.Value<string>("type");
                    if (string.IsNullOrEmpty(type))
                    {
                        // CLI 2.1.107+ omits top-level type for assistant turns — derive from message.role.
                        var mm = obj["message"] as JObject;
                        var role = mm?.Value<string>("role");
                        if (role == "assistant" || role == "user") type = role;
                        if (string.IsNullOrEmpty(type)) continue;
                    }

                    if (createdAt == 0)
                    {
                        var ts = obj.Value<string>("timestamp");
                        if (!string.IsNullOrEmpty(ts))
                        {
                            try { createdAt = DateTimeOffset.Parse(ts!).ToUnixTimeMilliseconds(); }
                            catch { }
                        }
                    }

                    if (type == "summary")
                    {
                        var s = obj.Value<string>("summary");
                        if (!string.IsNullOrEmpty(s)) cliSummary = s;
                        continue;
                    }

                    if (firstUserSummary == null && type == "user")
                    {
                        var msg = obj["message"] as JObject;
                        if (msg != null)
                        {
                            var text = CleanForSummary(ExtractUserText(msg["content"]));
                            if (!string.IsNullOrEmpty(text))
                                firstUserSummary = text!.Length > 60 ? text.Substring(0, 57) + "..." : text;
                        }
                    }

                    if (type == "user" || type == "assistant") messageCount++;

                    if (model == null)
                    {
                        var msg = obj["message"] as JObject;
                        var m = msg?.Value<string>("model");
                        if (!string.IsNullOrEmpty(m)) model = m;
                    }
                }
            }
            catch { return null; }

            if (messageCount == 0) return null;     // skip empty sessions

            var summary = !string.IsNullOrEmpty(cliSummary)
                ? (cliSummary!.Length > 60 ? cliSummary.Substring(0, 57) + "..." : cliSummary)
                : firstUserSummary;
            info.Summary = summary;
            info.MessageCount = messageCount;
            info.Model = model;
            if (createdAt > 0) info.StartTime = createdAt;
            return info;
        }

        /// <summary>
        /// Strip leading <c>&lt;file path="..."&gt;...&lt;/file&gt;</c> XML blocks and
        /// <c>[Active editor context: ...]</c> markers that the plugin/CLI prepend to user
        /// messages, so summaries read like the actual prompt.
        /// </summary>
        public static string CleanForSummary(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = FileBlockRx.Replace(s!, "");
            s = ActiveCtxRx.Replace(s, "");
            return s.Trim();
        }

        private static string ExtractUserText(JToken? content)
        {
            if (content == null) return "";
            if (content.Type == JTokenType.String) return content.Value<string>() ?? "";
            if (content is JArray arr)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in arr)
                {
                    if (part is JObject p && p.Value<string>("type") == "text")
                        sb.Append(p.Value<string>("text") ?? "");
                }
                return sb.ToString();
            }
            return "";
        }

        private static string EncodeProjectKey(string path)
            => path.Replace(':', '-').Replace('\\', '-').Replace('/', '-');

        private static bool IsUuid(string s)
            => !string.IsNullOrEmpty(s)
               && s.Length == 36
               && Guid.TryParse(s, out _);
    }
}
