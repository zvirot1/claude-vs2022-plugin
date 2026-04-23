using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.Cli
{
    /// <summary>
    /// Parses NDJSON lines from the Claude CLI process into typed CliMessage objects.
    /// Port of com.anthropic.claude.intellij.cli.NdjsonProtocolHandler.
    /// </summary>
    public class NdjsonProtocolHandler
    {
        public CliMessage? ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            try
            {
                var json = JObject.Parse(line);
                var type = json.Value<string>("type");
                if (type == null)
                    return null;

                return type switch
                {
                    "system" => ParseSystemInit(json),
                    "assistant" => ParseAssistantMessage(json),
                    "user" => ParseUserMessage(json),
                    "result" => ParseResultMessage(json),
                    "stream_event" => ParseStreamEvent(json),
                    "tool_use_permission" or "permission_request" or "tool_permission" => ParsePermissionRequest(json),
                    "control_request" => ParseControlRequest(json),
                    "rate_limit_event" => new CliMessage.RateLimitEvent
                    {
                        Message = json.Value<string>("message") ?? json["data"]?.Value<string>("message"),
                        ResetAtEpochSec = json.Value<long?>("reset_at") ?? json["data"]?.Value<long?>("reset_at")
                    },
                    "tool_use_summary" => ParseToolUseSummary(json),
                    _ when type.Contains("permission") => ParsePermissionRequest(json),
                    _ => null
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private CliMessage.SystemInit ParseSystemInit(JObject json)
        {
            var init = new CliMessage.SystemInit
            {
                Subtype = json.Value<string>("subtype"),
                SessionId = json.Value<string>("session_id"),
                Model = json.Value<string>("model"),
                Cwd = json.Value<string>("cwd"),
                PermissionMode = json.Value<string>("permissionMode")
            };

            var toolsList = json["tools"] as JArray;
            if (toolsList != null)
            {
                init.Tools = new List<string>();
                foreach (var tool in toolsList)
                    init.Tools.Add(tool.ToString());
            }

            return init;
        }

        private CliMessage.AssistantMessage ParseAssistantMessage(JObject json)
        {
            var msg = new CliMessage.AssistantMessage();
            var source = json["message"] as JObject ?? json;

            msg.StopReason = source.Value<string>("stop_reason");

            var contentList = source["content"] as JArray;
            if (contentList != null)
                msg.Content = ParseContentBlocks(contentList);

            var usageMap = source["usage"] as JObject;
            if (usageMap != null)
                msg.Usage = ParseUsageData(usageMap);

            return msg;
        }

        private CliMessage.UserMessage ParseUserMessage(JObject json)
        {
            var msg = new CliMessage.UserMessage();
            var source = json["message"] as JObject ?? json;

            var contentList = source["content"] as JArray;
            if (contentList != null)
                msg.Content = ParseContentBlocks(contentList);

            return msg;
        }

        private CliMessage.ResultMessage ParseResultMessage(JObject json)
        {
            var msg = new CliMessage.ResultMessage
            {
                Subtype = json.Value<string>("subtype"),
                Result = json.Value<string>("result"),
                SessionId = json.Value<string>("session_id"),
                CostUsd = json.Value<double?>("total_cost_usd") ?? json.Value<double?>("cost_usd") ?? 0.0,
                DurationMs = json.Value<long?>("duration_ms") ?? 0L,
                NumTurns = json.Value<int?>("num_turns") ?? 0,
                IsError = json.Value<bool?>("is_error") ?? false
            };

            var usageMap = json["usage"] as JObject;
            if (usageMap != null)
            {
                msg.InputTokens = usageMap.Value<int?>("input_tokens") ?? 0;
                msg.OutputTokens = usageMap.Value<int?>("output_tokens") ?? 0;
                msg.Usage = ParseUsageData(usageMap);
            }
            else
            {
                msg.InputTokens = json.Value<int?>("input_tokens") ?? 0;
                msg.OutputTokens = json.Value<int?>("output_tokens") ?? 0;
            }

            return msg;
        }

        private CliMessage.StreamEvent ParseStreamEvent(JObject json)
        {
            var evt = new CliMessage.StreamEvent
            {
                SessionId = json.Value<string>("session_id"),
                Uuid = json.Value<string>("uuid")
            };

            var eventData = json["event"] as JObject ?? json;

            evt.EventType = eventData.Value<string>("type") ?? json.Value<string>("event_type");
            evt.Index = eventData.Value<int?>("index") ?? 0;

            var contentBlockMap = eventData["content_block"] as JObject;
            if (contentBlockMap != null)
                evt.ContentBlock = ParseContentBlock(contentBlockMap);

            var deltaMap = eventData["delta"] as JObject;
            if (deltaMap != null)
                evt.Delta = ParseDelta(deltaMap);

            return evt;
        }

        private CliMessage.PermissionRequest ParsePermissionRequest(JObject json)
        {
            return new CliMessage.PermissionRequest
            {
                ToolUseId = json.Value<string>("tool_use_id"),
                RequestId = json.Value<string>("request_id"),
                IsControlRequest = json.Value<bool?>("control_request") ?? false,
                ToolName = json.Value<string>("tool_name"),
                Description = json.Value<string>("description"),
                ToolInput = json["tool_input"],
                RawJson = json
            };
        }

        private CliMessage.PermissionRequest ParseControlRequest(JObject json)
        {
            var msg = new CliMessage.PermissionRequest
            {
                RawJson = json,
                IsControlRequest = true,
                RequestId = json.Value<string>("request_id")
            };

            var request = json["request"] as JObject;
            if (request != null)
            {
                msg.ToolName = request.Value<string>("tool_name");
                msg.ToolInput = request["input"];

                var inputObj = request["input"] as JObject;
                if (inputObj != null)
                {
                    var cmd = inputObj.Value<string>("command");
                    if (cmd != null)
                    {
                        msg.Description = "Run: " + (cmd.Length > 80 ? cmd.Substring(0, 77) + "..." : cmd);
                    }
                    else if (inputObj.Count > 0)
                    {
                        var first = inputObj.First as JProperty;
                        if (first != null)
                        {
                            var val = first.Value?.ToString() ?? "";
                            msg.Description = first.Name + ": " + (val.Length > 80 ? val.Substring(0, 77) + "..." : val);
                        }
                    }
                }
            }

            return msg;
        }

        private CliMessage.ToolUseSummary ParseToolUseSummary(JObject json)
        {
            var msg = new CliMessage.ToolUseSummary
            {
                Summary = json.Value<string>("summary")
            };

            var idsArray = json["preceding_tool_use_ids"] as JArray;
            if (idsArray != null)
            {
                msg.ToolUseIds = new List<string>();
                foreach (var id in idsArray)
                    msg.ToolUseIds.Add(id.ToString());
            }

            var summary = msg.Summary;
            msg.IsFailed = summary != null &&
                (summary.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 summary.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0);

            return msg;
        }

        private List<CliMessage.ContentBlock> ParseContentBlocks(JArray contentList)
        {
            var blocks = new List<CliMessage.ContentBlock>();
            foreach (var item in contentList)
            {
                if (item is JObject obj)
                    blocks.Add(ParseContentBlock(obj));
            }
            return blocks;
        }

        private CliMessage.ContentBlock ParseContentBlock(JObject map)
        {
            return new CliMessage.ContentBlock
            {
                Type = map.Value<string>("type"),
                Text = map.Value<string>("text"),
                Id = map.Value<string>("id"),
                Name = map.Value<string>("name"),
                Content = map.Value<string>("content"),
                ToolUseId = map.Value<string>("tool_use_id"),
                IsError = map.Value<bool?>("is_error") ?? false,
                Input = map["input"]
            };
        }

        private CliMessage.Delta ParseDelta(JObject map)
        {
            return new CliMessage.Delta
            {
                Type = map.Value<string>("type"),
                Text = map.Value<string>("text"),
                PartialJson = map.Value<string>("partial_json"),
                StopReason = map.Value<string>("stop_reason")
            };
        }

        private CliMessage.UsageData ParseUsageData(JObject map)
        {
            return new CliMessage.UsageData
            {
                InputTokens = map.Value<int?>("input_tokens") ?? 0,
                OutputTokens = map.Value<int?>("output_tokens") ?? 0
            };
        }
    }
}
