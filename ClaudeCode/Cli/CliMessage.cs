using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.Cli
{
    /// <summary>
    /// Base class for all CLI message types exchanged with the Claude CLI process via NDJSON.
    /// Port of com.anthropic.claude.intellij.cli.CliMessage.
    /// </summary>
    public abstract class CliMessage
    {
        public string Type { get; }

        protected CliMessage(string type) => Type = type;

        /// <summary>Sentinel for recognized-but-ignored messages.</summary>
        public static readonly CliMessage Ignored = new IgnoredMessage();
        private sealed class IgnoredMessage : CliMessage { public IgnoredMessage() : base("ignored") { } }

        #region Incoming message types (CLI → Plugin)

        public class SystemInit : CliMessage
        {
            public string? Subtype { get; set; }
            public string? SessionId { get; set; }
            public string? Model { get; set; }
            public string? Cwd { get; set; }
            public List<string>? Tools { get; set; }
            public string? PermissionMode { get; set; }
            public SystemInit() : base("system") { }
        }

        public class AssistantMessage : CliMessage
        {
            public List<ContentBlock> Content { get; set; } = new();
            public string? StopReason { get; set; }
            public UsageData? Usage { get; set; }
            public AssistantMessage() : base("assistant") { }
        }

        public class UserMessage : CliMessage
        {
            public List<ContentBlock> Content { get; set; } = new();
            public UserMessage() : base("user") { }
        }

        public class ResultMessage : CliMessage
        {
            public string? Subtype { get; set; }
            public string? Result { get; set; }
            public string? SessionId { get; set; }
            public double CostUsd { get; set; }
            public int InputTokens { get; set; }
            public int OutputTokens { get; set; }
            public long DurationMs { get; set; }
            public int NumTurns { get; set; }
            public bool IsError { get; set; }
            public UsageData? Usage { get; set; }
            public ResultMessage() : base("result") { }
        }

        public class StreamEvent : CliMessage
        {
            public string? EventType { get; set; }
            public int Index { get; set; }
            public new ContentBlock? ContentBlock { get; set; }
            public new Delta? Delta { get; set; }
            public string? SessionId { get; set; }
            public string? Uuid { get; set; }
            public StreamEvent() : base("stream_event") { }
        }

        public class PermissionRequest : CliMessage
        {
            public string? ToolUseId { get; set; }
            public string? RequestId { get; set; }
            public bool IsControlRequest { get; set; }
            public string? ToolName { get; set; }
            public string? Description { get; set; }
            public object? ToolInput { get; set; }
            public JObject? RawJson { get; set; }
            public PermissionRequest() : base("permission_request") { }
        }

        public class RateLimitEvent : CliMessage
        {
            public string? Message { get; set; }
            public long? ResetAtEpochSec { get; set; }
            public RateLimitEvent() : base("rate_limit") { }
        }

        public class ToolUseSummary : CliMessage
        {
            public string? Summary { get; set; }
            public List<string>? ToolUseIds { get; set; }
            public bool IsFailed { get; set; }
            public ToolUseSummary() : base("tool_use_summary") { }
        }

        #endregion

        #region Nested data types

        public class ContentBlock
        {
            public string? Type { get; set; }
            public string? Text { get; set; }
            public string? Id { get; set; }
            public string? Name { get; set; }
            public object? Input { get; set; }
            public string? Content { get; set; }
            public string? ToolUseId { get; set; }
            public bool IsError { get; set; }

            public string GetInputAsString()
            {
                if (Input == null) return "";
                if (Input is string s) return s;
                return Input.ToString() ?? "";
            }
        }

        public class Delta
        {
            public string? Type { get; set; }
            public string? Text { get; set; }
            public string? PartialJson { get; set; }
            public string? StopReason { get; set; }
        }

        public class UsageData
        {
            public int InputTokens { get; set; }
            public int OutputTokens { get; set; }
        }

        #endregion

        #region Outgoing message builders (Plugin → CLI)

        public static string CreateUserInputJson(string userContent)
        {
            var obj = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = userContent
                }
            };
            return obj.ToString(Formatting.None);
        }

        public static string CreateUserInputJsonRich(string textContent, List<byte[]>? imageDataList)
        {
            if (imageDataList == null || imageDataList.Count == 0)
                return CreateUserInputJson(textContent);

            var contentArray = new JArray();
            foreach (var imageBytes in imageDataList)
            {
                var base64 = System.Convert.ToBase64String(imageBytes);
                contentArray.Add(new JObject
                {
                    ["type"] = "image",
                    ["source"] = new JObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = base64
                    }
                });
            }
            contentArray.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = textContent
            });

            var obj = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = contentArray
                }
            };
            return obj.ToString(Formatting.None);
        }

        public static string CreatePermissionResponse(string toolUseId, bool allow)
        {
            var obj = new JObject
            {
                ["type"] = "permission_response",
                ["tool_use_id"] = toolUseId,
                ["permission"] = allow ? "allow" : "deny"
            };
            return obj.ToString(Formatting.None);
        }

        public static string CreateControlResponse(string requestId, bool allow, object? toolInput = null)
        {
            JObject innerResponse;
            if (allow)
            {
                var inputToken = toolInput != null ? JToken.FromObject(toolInput) : new JObject();
                innerResponse = new JObject
                {
                    ["behavior"] = "allow",
                    ["updatedInput"] = inputToken
                };
            }
            else
            {
                innerResponse = new JObject
                {
                    ["behavior"] = "deny",
                    ["message"] = "User denied"
                };
            }

            var obj = new JObject
            {
                ["type"] = "control_response",
                ["response"] = new JObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = requestId,
                    ["response"] = innerResponse
                }
            };
            return obj.ToString(Formatting.None);
        }

        #endregion
    }
}
