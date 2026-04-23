namespace ClaudeCode.Model
{
    /// <summary>
    /// Listener for conversation model changes.
    /// All callbacks may be called from a background thread.
    /// Port of com.anthropic.claude.intellij.model.IConversationListener.
    /// </summary>
    public interface IConversationListener
    {
        void OnSessionInitialized(SessionInfo info);
        void OnUserMessageAdded(MessageBlock block);
        void OnAssistantMessageStarted(MessageBlock block);
        void OnStreamingTextAppended(MessageBlock block, string delta);
        void OnToolCallStarted(MessageBlock block, MessageBlock.ToolCallSegment toolCall);
        void OnToolCallInputDelta(MessageBlock block, MessageBlock.ToolCallSegment toolCall, string delta);
        void OnToolCallInputComplete(MessageBlock block, MessageBlock.ToolCallSegment toolCall);
        void OnToolCallCompleted(MessageBlock block, MessageBlock.ToolCallSegment toolCall);
        void OnAssistantMessageCompleted(MessageBlock block);
        void OnResultReceived(UsageInfo usage);
        void OnPermissionRequested(string? toolUseId, string toolName, string description, string? requestId, object? toolInput);
        void OnExtendedThinkingStarted();
        void OnExtendedThinkingEnded();
        void OnError(string error);
        void OnConversationCleared();
        void OnRateLimit(string? message, long? resetAtEpochSec);
    }

    /// <summary>
    /// Base adapter with empty default implementations.
    /// </summary>
    public abstract class ConversationListenerAdapter : IConversationListener
    {
        public virtual void OnSessionInitialized(SessionInfo info) { }
        public virtual void OnUserMessageAdded(MessageBlock block) { }
        public virtual void OnAssistantMessageStarted(MessageBlock block) { }
        public virtual void OnStreamingTextAppended(MessageBlock block, string delta) { }
        public virtual void OnToolCallStarted(MessageBlock block, MessageBlock.ToolCallSegment toolCall) { }
        public virtual void OnToolCallInputDelta(MessageBlock block, MessageBlock.ToolCallSegment toolCall, string delta) { }
        public virtual void OnToolCallInputComplete(MessageBlock block, MessageBlock.ToolCallSegment toolCall) { }
        public virtual void OnToolCallCompleted(MessageBlock block, MessageBlock.ToolCallSegment toolCall) { }
        public virtual void OnAssistantMessageCompleted(MessageBlock block) { }
        public virtual void OnResultReceived(UsageInfo usage) { }
        public virtual void OnPermissionRequested(string? toolUseId, string toolName, string description, string? requestId, object? toolInput) { }
        public virtual void OnExtendedThinkingStarted() { }
        public virtual void OnExtendedThinkingEnded() { }
        public virtual void OnError(string error) { }
        public virtual void OnConversationCleared() { }
        public virtual void OnRateLimit(string? message, long? resetAtEpochSec) { }
    }
}
