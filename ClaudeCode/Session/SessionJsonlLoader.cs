using System;
using System.Collections.Generic;
using System.IO;
using ClaudeCode.Model;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.Session
{
    /// <summary>
    /// Reads a resumed Claude session's JSONL history file (stored by the CLI at
    /// <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl</c>) and converts the user/
    /// assistant turns into <see cref="MessageBlock"/>s so the UI can display past
    /// conversation after <c>--resume</c>. Port of the history-load step from Eclipse/IntelliJ.
    /// </summary>
    public static class SessionJsonlLoader
    {
        public static List<MessageBlock> Load(string sessionId, string workingDir)
        {
            var result = new List<MessageBlock>();
            try
            {
                var path = BuildPath(sessionId, workingDir);
                if (path == null || !File.Exists(path)) return result;

                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject obj;
                    try { obj = JObject.Parse(line); } catch { continue; }

                    var type = obj.Value<string>("type");
                    if (type != "user" && type != "assistant") continue;

                    var message = obj["message"] as JObject;
                    if (message == null) continue;

                    if (type == "user")
                    {
                        var content = message["content"];
                        string text = ExtractText(content);
                        if (string.IsNullOrEmpty(text)) continue;
                        var block = new MessageBlock(MessageBlock.Role.User);
                        var seg = new MessageBlock.TextSegment();
                        seg.AppendText(text);
                        block.AddSegment(seg);
                        result.Add(block);
                    }
                    else // assistant
                    {
                        var contentArr = message["content"] as JArray;
                        if (contentArr == null || contentArr.Count == 0) continue;
                        var block = new MessageBlock(MessageBlock.Role.Assistant);
                        foreach (var item in contentArr)
                        {
                            var itemType = item.Value<string>("type");
                            if (itemType == "text")
                            {
                                var seg = new MessageBlock.TextSegment();
                                seg.AppendText(item.Value<string>("text") ?? "");
                                block.AddSegment(seg);
                            }
                            else if (itemType == "tool_use")
                            {
                                block.AddSegment(new MessageBlock.ToolCallSegment
                                {
                                    ToolId = item.Value<string>("id"),
                                    ToolName = item.Value<string>("name"),
                                    Input = item["input"]?.ToString() ?? "",
                                    Status = MessageBlock.ToolStatus.Completed
                                });
                            }
                        }
                        if (block.Segments.Count > 0) result.Add(block);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionJsonlLoader] {ex.Message}");
            }
            return result;
        }

        private static string ExtractText(JToken? content)
        {
            if (content == null) return "";
            if (content.Type == JTokenType.String) return content.Value<string>() ?? "";
            if (content is JArray arr)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in arr)
                {
                    if (item.Value<string>("type") == "text")
                        sb.AppendLine(item.Value<string>("text") ?? "");
                }
                return sb.ToString().TrimEnd();
            }
            return "";
        }

        private static string? BuildPath(string sessionId, string workingDir)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(workingDir)) return null;
            // CLI encodes cwd: replace separators (\, /, :) with '-'
            var encoded = workingDir.Replace('\\', '-').Replace('/', '-').Replace(':', '-');
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude", "projects", encoded, sessionId + ".jsonl");
            return path;
        }
    }
}
