using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCode.Cli;
using ClaudeCode.Model;

namespace ClaudeCode.UI
{
    /// <summary>
    /// Status bar info bar item showing Claude CLI state.
    /// Port of com.anthropic.claude.intellij.statusbar.ClaudeStatusBarWidget.
    /// Uses VS InfoBar approach - displays as a simple text element.
    /// </summary>
    public class ClaudeStatusBar : IConversationListener, ICliStateListener
    {
        public enum DisplayState { Disconnected, Ready, Thinking, Error }

        private DisplayState _state = DisplayState.Disconnected;
        private string? _modelName;

        public DisplayState CurrentState => _state;
        public string? ModelName => _modelName;

        public event Action? StateChanged;

        public string GetStatusText()
        {
            var dot = _state switch
            {
                DisplayState.Ready => "\u2022",     // green dot
                DisplayState.Thinking => "\u25CF",  // blue dot
                DisplayState.Error => "\u2716",     // red x
                _ => "\u25CB"                        // empty circle
            };

            var stateText = _state switch
            {
                DisplayState.Ready => "Ready",
                DisplayState.Thinking => "Thinking...",
                DisplayState.Error => "Error",
                _ => "Disconnected"
            };

            var model = ShortenModelName(_modelName);
            return model != null ? $"{dot} Claude ({model}) - {stateText}" : $"{dot} Claude - {stateText}";
        }

        void IConversationListener.OnSessionInitialized(SessionInfo info)
        {
            _modelName = info.Model;
            _state = DisplayState.Ready;
            StateChanged?.Invoke();
        }

        void IConversationListener.OnAssistantMessageStarted(MessageBlock block)
        {
            _state = DisplayState.Thinking;
            StateChanged?.Invoke();
        }

        void IConversationListener.OnResultReceived(UsageInfo usage)
        {
            _state = DisplayState.Ready;
            StateChanged?.Invoke();
        }

        void IConversationListener.OnError(string error)
        {
            _state = DisplayState.Error;
            StateChanged?.Invoke();
        }

        void ICliStateListener.OnStateChanged(ClaudeCliManager.ProcessState oldState, ClaudeCliManager.ProcessState newState)
        {
            _state = newState switch
            {
                ClaudeCliManager.ProcessState.Running => DisplayState.Ready,
                ClaudeCliManager.ProcessState.Error => DisplayState.Error,
                _ => DisplayState.Disconnected
            };
            StateChanged?.Invoke();
        }

        // Default no-op implementations for IConversationListener
        void IConversationListener.OnUserMessageAdded(MessageBlock block) { }
        void IConversationListener.OnStreamingTextAppended(MessageBlock block, string delta) { }
        void IConversationListener.OnToolCallStarted(MessageBlock block, MessageBlock.ToolCallSegment toolCall) { }
        void IConversationListener.OnToolCallInputDelta(MessageBlock block, MessageBlock.ToolCallSegment toolCall, string delta) { }
        void IConversationListener.OnToolCallInputComplete(MessageBlock block, MessageBlock.ToolCallSegment toolCall) { }
        void IConversationListener.OnToolCallCompleted(MessageBlock block, MessageBlock.ToolCallSegment toolCall) { }
        void IConversationListener.OnAssistantMessageCompleted(MessageBlock block) { }
        void IConversationListener.OnPermissionRequested(string? toolUseId, string toolName, string description, string? requestId, object? toolInput) { }
        void IConversationListener.OnExtendedThinkingStarted() { }
        void IConversationListener.OnExtendedThinkingEnded() { }

        void IConversationListener.OnConversationCleared()
        {
            // When a conversation is cleared, reset the status bar (clear model name, reset state).
            // Don't go to Disconnected though — the CLI is likely still running, just empty conversation.
            _modelName = null;
            // Keep current connection state (Ready/Error) but clear contextual info
            StateChanged?.Invoke();
        }

        void IConversationListener.OnRateLimit(string? message, long? resetAtEpochSec) { }
        void IConversationListener.OnSilentEmptyShouldRetry(string lastUserPrompt) { }

        private static string? ShortenModelName(string? model)
        {
            if (model == null) return null;
            // "claude-3-5-sonnet-20241022" → "3-5-sonnet"
            if (model.StartsWith("claude-"))
            {
                var rest = model.Substring(7);
                var dashIdx = rest.LastIndexOf('-');
                if (dashIdx > 0 && rest.Length > dashIdx + 1 && char.IsDigit(rest[dashIdx + 1]))
                    return rest.Substring(0, dashIdx);
                return rest;
            }
            return model;
        }
    }
}
