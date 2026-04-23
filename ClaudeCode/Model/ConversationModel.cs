using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ClaudeCode.Cli;

namespace ClaudeCode.Model
{
    /// <summary>
    /// Central conversation model bridging CLI protocol and UI.
    /// Implements ICliMessageListener to receive messages from CLI,
    /// notifies IConversationListeners of changes.
    /// Port of com.anthropic.claude.intellij.model.ConversationModel.
    /// </summary>
    public class ConversationModel : CliMessageListenerAdapter
    {
        private readonly List<MessageBlock> _messages = new();
        private readonly List<IConversationListener> _listeners = new();
        private readonly object _lock = new();

        public SessionInfo? SessionInfo { get; set; }
        public UsageInfo CumulativeUsage { get; } = new();
        public MessageBlock? CurrentStreamingBlock { get; private set; }
        public bool IsStreaming => CurrentStreamingBlock != null;
        public string? LastPermissionToolName { get; private set; }

        private readonly ConcurrentDictionary<int, MessageBlock.ToolCallSegment> _activeToolCalls = new();
        private readonly HashSet<string> _firedToolIds = new();
        private volatile bool _usingStreamEvents;
        /// <summary>True once stream events have been used in this session. Never reset.</summary>
        private volatile bool _hasUsedStreamEvents;

        // Stream activity timeout (port from Eclipse Phase 5)
        // Detects stuck sessions: if no stream activity for 45s AND no tool is running, fire timeout.
        private const int StreamTimeoutMs = 45000;
        private System.Threading.Timer? _streamTimer;
        private readonly object _streamTimerLock = new();
        private volatile bool _streamActive;

        /// <summary>Returns true if any tool call is currently in Running status.</summary>
        public bool HasRunningToolCalls()
        {
            foreach (var kv in _activeToolCalls)
            {
                if (kv.Value.Status == MessageBlock.ToolStatus.Running)
                    return true;
            }
            return false;
        }

        /// <summary>Called on every stream event to reset the inactivity timer.</summary>
        public void TouchStreamActivity()
        {
            _streamActive = true;
            lock (_streamTimerLock)
            {
                _streamTimer?.Dispose();
                _streamTimer = new System.Threading.Timer(_ => CheckStreamingTimeout(), null, StreamTimeoutMs, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>Cancels any pending stream timeout check.</summary>
        public void CancelStreamingTimeout()
        {
            _streamActive = false;
            lock (_streamTimerLock)
            {
                _streamTimer?.Dispose();
                _streamTimer = null;
            }
        }

        private void CheckStreamingTimeout()
        {
            if (!_streamActive) return;
            // Don't fire timeout while a tool is executing (long Maven/MSBuild commands)
            if (HasRunningToolCalls())
            {
                // Reschedule for another 45s
                TouchStreamActivity();
                return;
            }
            _streamActive = false;
            MarkActiveToolCallsFailed("Stream timeout (45s inactivity)");
            FireError("Stream timeout — no activity for 45 seconds. The CLI may be stuck.");
        }

        #region ICliMessageListener

        public override void OnMessage(CliMessage message)
        {
            switch (message)
            {
                case CliMessage.SystemInit init: HandleSystemInit(init); break;
                case CliMessage.AssistantMessage msg: HandleAssistantMessage(msg); break;
                case CliMessage.UserMessage msg: HandleUserMessage(msg); break;
                case CliMessage.StreamEvent evt: HandleStreamEvent(evt); break;
                case CliMessage.ResultMessage result: HandleResult(result); break;
                case CliMessage.PermissionRequest req: HandlePermissionRequest(req); break;
                case CliMessage.ToolUseSummary summary: HandleToolUseSummary(summary); break;
                case CliMessage.RateLimitEvent rl:
                    foreach (var l in GetListeners())
                        try { l.OnRateLimit(rl.Message, rl.ResetAtEpochSec); } catch { }
                    break;
            }
        }

        public override void OnParseError(string rawLine, Exception error) { }

        public override void OnConnectionError(Exception error)
        {
            CancelStreamingTimeout();
            MarkActiveToolCallsFailed("Connection lost");
            FireError("Connection to Claude CLI lost: " + error.Message);
        }

        #endregion

        #region Public API

        public void AddUserMessage(string content)
        {
            var block = new MessageBlock(MessageBlock.Role.User);
            var textSeg = new MessageBlock.TextSegment();
            textSeg.AppendText(content);
            block.AddSegment(textSeg);
            lock (_lock) { _messages.Add(block); }
            FireUserMessageAdded(block);
        }

        public List<MessageBlock> GetMessages()
        {
            lock (_lock) { return new List<MessageBlock>(_messages); }
        }

        public int MessageCount { get { lock (_lock) { return _messages.Count; } } }

        public void Clear()
        {
            lock (_lock) { _messages.Clear(); }
            CurrentStreamingBlock = null;
            _usingStreamEvents = false;
            _hasUsedStreamEvents = false;
            _lastAssistantHash = null;
            _activeToolCalls.Clear();
            _firedToolIds.Clear();
            CancelStreamingTimeout();
            CumulativeUsage.Reset();
            FireConversationCleared();
        }

        public void LoadHistory(List<MessageBlock> historicalBlocks)
        {
            lock (_lock) { _messages.AddRange(historicalBlocks); }
            foreach (var block in historicalBlocks)
            {
                if (block.MessageRole == MessageBlock.Role.User)
                    FireUserMessageAdded(block);
                else if (block.MessageRole == MessageBlock.Role.Assistant)
                {
                    FireAssistantMessageStarted(block);
                    FireAssistantMessageCompleted(block);
                }
            }
        }

        public void AddListener(IConversationListener listener)
        {
            lock (_lock) { if (!_listeners.Contains(listener)) _listeners.Add(listener); }
        }

        public void RemoveListener(IConversationListener listener)
        {
            lock (_lock) { _listeners.Remove(listener); }
        }

        #endregion

        #region Message Handlers

        private void HandleSystemInit(CliMessage.SystemInit init)
        {
            SessionInfo = new SessionInfo(init.SessionId ?? "");
            SessionInfo.Model = init.Model;
            SessionInfo.WorkingDirectory = init.Cwd;
            SessionInfo.PermissionMode = init.PermissionMode;
            FireSessionInitialized(SessionInfo);
        }

        private string? _lastAssistantHash;

        private void HandleAssistantMessage(CliMessage.AssistantMessage msg)
        {
            // Always ignore assistant snapshot messages when streaming has been used.
            // The --include-partial-messages flag causes the CLI to send assistant snapshots
            // AFTER result messages, which would create duplicate tool calls.
            // Stream events are authoritative; assistant messages are redundant.
            if (_usingStreamEvents || _hasUsedStreamEvents) return;

            // B2: Content-based dedup — skip if the exact same message text+toolIds was just delivered.
            // Guards against edge cases where the CLI sends the same assistant block twice without flipping flags.
            if (msg.Content.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var cb in msg.Content)
                {
                    sb.Append(cb.Type).Append('|').Append(cb.Text ?? "").Append('|').Append(cb.Id ?? "").Append('\n');
                }
                var hash = sb.ToString().GetHashCode().ToString("X");
                if (hash == _lastAssistantHash) return;
                _lastAssistantHash = hash;
            }

            if (msg.Content.Count > 0)
            {
                var block = new MessageBlock(MessageBlock.Role.Assistant);
                foreach (var cb in msg.Content)
                {
                    if (cb.Type == "text")
                    {
                        var seg = new MessageBlock.TextSegment();
                        seg.AppendText(cb.Text ?? "");
                        block.AddSegment(seg);
                    }
                    else if (cb.Type == "tool_use")
                    {
                        var seg = new MessageBlock.ToolCallSegment
                        {
                            ToolId = cb.Id,
                            ToolName = cb.Name,
                            Input = cb.GetInputAsString(),
                            Status = MessageBlock.ToolStatus.Completed
                        };
                        block.AddSegment(seg);
                    }
                }
                lock (_lock) { _messages.Add(block); }
                FireAssistantMessageStarted(block);
                FireAssistantMessageCompleted(block);
            }
        }

        private void HandleUserMessage(CliMessage.UserMessage msg)
        {
            foreach (var cb in msg.Content)
            {
                if (cb.Type == "tool_result")
                    UpdateToolCallResult(cb);
            }
        }

        private void HandleStreamEvent(CliMessage.StreamEvent evt)
        {
            // Reset the 45s inactivity timer on every stream event
            TouchStreamActivity();

            switch (evt.EventType)
            {
                case "message_start": HandleMessageStart(); break;
                case "content_block_start": HandleContentBlockStart(evt); break;
                case "content_block_delta": HandleContentBlockDelta(evt); break;
                case "content_block_stop": HandleContentBlockStop(evt); break;
                case "message_delta": break;
                case "message_stop": HandleMessageStop(); break;
            }
        }

        private void HandleMessageStart()
        {
            _usingStreamEvents = true;
            _hasUsedStreamEvents = true;
            _firedToolIds.Clear();
            CurrentStreamingBlock = new MessageBlock(MessageBlock.Role.Assistant);
            lock (_lock) { _messages.Add(CurrentStreamingBlock); }
            _activeToolCalls.Clear();
        }

        private void HandleContentBlockStart(CliMessage.StreamEvent evt)
        {
            bool fireStarted = false;
            if (CurrentStreamingBlock == null)
            {
                _usingStreamEvents = true;
                CurrentStreamingBlock = new MessageBlock(MessageBlock.Role.Assistant);
                lock (_lock) { _messages.Add(CurrentStreamingBlock); }
                fireStarted = true;
            }
            else if (CurrentStreamingBlock.Segments.Count == 0)
            {
                fireStarted = true;
            }

            var cb = evt.ContentBlock;
            if (cb != null)
            {
                if (cb.Type == "thinking")
                {
                    FireExtendedThinkingStarted();
                }
                else if (cb.Type == "text")
                {
                    FireExtendedThinkingEnded();
                    CurrentStreamingBlock.GetOrCreateLastTextSegment();
                    if (fireStarted) FireAssistantMessageStarted(CurrentStreamingBlock);
                }
                else if (cb.Type == "tool_use")
                {
                    var toolSeg = new MessageBlock.ToolCallSegment
                    {
                        ToolId = cb.Id,
                        ToolName = cb.Name,
                        Status = MessageBlock.ToolStatus.Running
                    };
                    if (fireStarted) FireAssistantMessageStarted(CurrentStreamingBlock);
                    CurrentStreamingBlock.AddSegment(toolSeg);
                    _activeToolCalls[evt.Index] = toolSeg;
                    FireToolCallStarted(CurrentStreamingBlock, toolSeg);
                }
            }
            else if (fireStarted)
            {
                FireAssistantMessageStarted(CurrentStreamingBlock);
            }
        }

        private void HandleContentBlockDelta(CliMessage.StreamEvent evt)
        {
            if (CurrentStreamingBlock == null || evt.Delta == null) return;

            if (evt.Delta.Type == "text_delta" && evt.Delta.Text != null)
            {
                var textSeg = CurrentStreamingBlock.GetOrCreateLastTextSegment();
                textSeg.AppendText(evt.Delta.Text);
                FireStreamingTextAppended(CurrentStreamingBlock, evt.Delta.Text);
            }
            else if (evt.Delta.Type == "input_json_delta")
            {
                if (_activeToolCalls.TryGetValue(evt.Index, out var toolSeg))
                {
                    // Early-return: don't overwrite a completed/failed tool with stale Running delta
                    // (Port from Eclipse Phase 6 — fixes "stuck on Running" bug)
                    if (toolSeg.Status == MessageBlock.ToolStatus.Completed ||
                        toolSeg.Status == MessageBlock.ToolStatus.Failed)
                        return;

                    var inputDelta = evt.Delta.PartialJson ?? evt.Delta.Text;
                    if (inputDelta != null)
                    {
                        toolSeg.AppendInput(inputDelta);
                        FireToolCallInputDelta(CurrentStreamingBlock, toolSeg, inputDelta);
                    }
                }
            }
        }

        private void HandleContentBlockStop(CliMessage.StreamEvent evt)
        {
            if (_activeToolCalls.TryGetValue(evt.Index, out var toolSeg))
            {
                toolSeg.Status = MessageBlock.ToolStatus.Running;
                if (CurrentStreamingBlock != null)
                    FireToolCallInputComplete(CurrentStreamingBlock, toolSeg);
            }
        }

        private void HandleMessageStop()
        {
            if (CurrentStreamingBlock != null)
            {
                FireAssistantMessageCompleted(CurrentStreamingBlock);
                CurrentStreamingBlock = null;
            }
        }

        private void HandleResult(CliMessage.ResultMessage result)
        {
            _usingStreamEvents = false;
            CancelStreamingTimeout();
            CumulativeUsage.AddUsage(result.InputTokens, result.OutputTokens, result.CostUsd, result.DurationMs, result.NumTurns);

            if (SessionInfo != null)
            {
                SessionInfo.SessionId = result.SessionId;
                lock (_lock) { SessionInfo.MessageCount = _messages.Count; }
                SessionInfo.Touch();
            }

            if (result.IsError && result.Result != null)
                FireError(result.Result);

            if (CurrentStreamingBlock != null)
            {
                FireAssistantMessageCompleted(CurrentStreamingBlock);
                CurrentStreamingBlock = null;
            }

            SweepAllRunningToolCalls();
            _activeToolCalls.Clear();
            FireResultReceived(CumulativeUsage);
        }

        private void HandlePermissionRequest(CliMessage.PermissionRequest req)
        {
            var toolName = req.ToolName ?? "Unknown tool";
            var description = req.Description ?? $"Claude wants to use: {toolName}";
            LastPermissionToolName = toolName;
            FirePermissionRequested(req.ToolUseId, toolName, description, req.RequestId, req.ToolInput);
        }

        private void UpdateToolCallResult(CliMessage.ContentBlock toolResult)
        {
            var toolUseId = toolResult.ToolUseId;
            if (toolUseId == null) return;

            List<MessageBlock> snapshot;
            lock (_lock) { snapshot = new List<MessageBlock>(_messages); }

            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                var toolSeg = snapshot[i].FindToolCall(toolUseId);
                if (toolSeg != null)
                {
                    toolSeg.Output = toolResult.Content;
                    bool isFailed = toolResult.IsError ||
                        (toolResult.Content?.Contains("<tool_use_error>") ?? false);
                    toolSeg.Status = isFailed ? MessageBlock.ToolStatus.Failed : MessageBlock.ToolStatus.Completed;
                    FireToolCallCompleted(snapshot[i], toolSeg);
                    break;
                }
            }
        }

        private void HandleToolUseSummary(CliMessage.ToolUseSummary summary)
        {
            if (summary.ToolUseIds == null || summary.ToolUseIds.Count == 0) return;

            List<MessageBlock> snapshot;
            lock (_lock) { snapshot = new List<MessageBlock>(_messages); }

            var status = summary.IsFailed ? MessageBlock.ToolStatus.Failed : MessageBlock.ToolStatus.Completed;

            foreach (var toolUseId in summary.ToolUseIds)
            {
                // Remove from active tracking
                var toRemove = _activeToolCalls.Where(kv => kv.Value.ToolId == toolUseId).Select(kv => kv.Key).ToList();
                foreach (var key in toRemove)
                    _activeToolCalls.TryRemove(key, out _);

                for (int i = snapshot.Count - 1; i >= 0; i--)
                {
                    var toolSeg = snapshot[i].FindToolCall(toolUseId);
                    if (toolSeg != null)
                    {
                        if (summary.Summary != null) toolSeg.Output = summary.Summary;
                        toolSeg.Status = status;
                        FireToolCallCompleted(snapshot[i], toolSeg);
                        break;
                    }
                }
            }
        }

        public void MarkActiveToolCallsFailed(string reason)
        {
            foreach (var entry in _activeToolCalls)
            {
                if (entry.Value.Status == MessageBlock.ToolStatus.Running)
                {
                    entry.Value.Status = MessageBlock.ToolStatus.Failed;
                    entry.Value.Output = "\u26a0 " + reason;

                    List<MessageBlock> snapshot;
                    lock (_lock) { snapshot = new List<MessageBlock>(_messages); }
                    foreach (var block in snapshot)
                    {
                        if (block.FindToolCall(entry.Value.ToolId!) != null)
                        {
                            FireToolCallCompleted(block, entry.Value);
                            break;
                        }
                    }
                }
            }
            _activeToolCalls.Clear();
        }

        private void SweepAllRunningToolCalls()
        {
            List<MessageBlock> snapshot;
            lock (_lock) { snapshot = new List<MessageBlock>(_messages); }
            foreach (var block in snapshot)
            {
                foreach (var seg in block.Segments)
                {
                    if (seg is MessageBlock.ToolCallSegment tc && tc.Status == MessageBlock.ToolStatus.Running)
                    {
                        tc.Status = MessageBlock.ToolStatus.Completed;
                        FireToolCallCompleted(block, tc);
                    }
                }
            }
        }

        #endregion

        #region Event Firing

        private List<IConversationListener> GetListeners()
        {
            lock (_lock) { return new List<IConversationListener>(_listeners); }
        }

        private void FireSessionInitialized(SessionInfo info) { foreach (var l in GetListeners()) try { l.OnSessionInitialized(info); } catch { } }
        private void FireUserMessageAdded(MessageBlock block) { foreach (var l in GetListeners()) try { l.OnUserMessageAdded(block); } catch { } }
        private void FireAssistantMessageStarted(MessageBlock block) { foreach (var l in GetListeners()) try { l.OnAssistantMessageStarted(block); } catch { } }
        private void FireStreamingTextAppended(MessageBlock block, string delta) { foreach (var l in GetListeners()) try { l.OnStreamingTextAppended(block, delta); } catch { } }
        private void FireToolCallStarted(MessageBlock block, MessageBlock.ToolCallSegment tc) {
            // Dedup: skip if this toolId was already fired
            if (tc.ToolId != null && !_firedToolIds.Add(tc.ToolId)) return;
            foreach (var l in GetListeners()) try { l.OnToolCallStarted(block, tc); } catch { }
        }
        private void FireToolCallInputDelta(MessageBlock block, MessageBlock.ToolCallSegment tc, string delta) { foreach (var l in GetListeners()) try { l.OnToolCallInputDelta(block, tc, delta); } catch { } }
        private void FireToolCallInputComplete(MessageBlock block, MessageBlock.ToolCallSegment tc) { foreach (var l in GetListeners()) try { l.OnToolCallInputComplete(block, tc); } catch { } }
        private void FireToolCallCompleted(MessageBlock block, MessageBlock.ToolCallSegment tc) { foreach (var l in GetListeners()) try { l.OnToolCallCompleted(block, tc); } catch { } }
        private void FireAssistantMessageCompleted(MessageBlock block) { foreach (var l in GetListeners()) try { l.OnAssistantMessageCompleted(block); } catch { } }
        private void FireResultReceived(UsageInfo usage) { foreach (var l in GetListeners()) try { l.OnResultReceived(usage); } catch { } }
        private void FirePermissionRequested(string? toolUseId, string toolName, string desc, string? requestId, object? toolInput) { foreach (var l in GetListeners()) try { l.OnPermissionRequested(toolUseId, toolName, desc, requestId, toolInput); } catch { } }
        private void FireExtendedThinkingStarted() { foreach (var l in GetListeners()) try { l.OnExtendedThinkingStarted(); } catch { } }
        private void FireExtendedThinkingEnded() { foreach (var l in GetListeners()) try { l.OnExtendedThinkingEnded(); } catch { } }
        private void FireError(string error) { foreach (var l in GetListeners()) try { l.OnError(error); } catch { } }
        private void FireConversationCleared() { foreach (var l in GetListeners()) try { l.OnConversationCleared(); } catch { } }

        #endregion
    }
}
