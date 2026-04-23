using System;

namespace ClaudeCode.Cli
{
    /// <summary>
    /// Listener for NDJSON messages received from the Claude CLI process.
    /// </summary>
    public interface ICliMessageListener
    {
        void OnMessage(CliMessage message);
        void OnParseError(string rawLine, Exception error);
        void OnConnectionError(Exception error);
    }

    /// <summary>
    /// Base adapter with empty default implementations.
    /// </summary>
    public abstract class CliMessageListenerAdapter : ICliMessageListener
    {
        public abstract void OnMessage(CliMessage message);
        public virtual void OnParseError(string rawLine, Exception error) { }
        public virtual void OnConnectionError(Exception error) { }
    }
}
