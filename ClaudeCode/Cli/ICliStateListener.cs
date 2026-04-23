namespace ClaudeCode.Cli
{
    /// <summary>
    /// Listener for CLI process state changes.
    /// </summary>
    public interface ICliStateListener
    {
        void OnStateChanged(ClaudeCliManager.ProcessState oldState, ClaudeCliManager.ProcessState newState);
    }
}
