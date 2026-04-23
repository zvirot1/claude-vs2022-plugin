using System;
using System.Collections.Concurrent;
using ClaudeCode.Cli;
using ClaudeCode.Diff;
using ClaudeCode.Model;
using ClaudeCode.Session;
using ClaudeCode.UI;

namespace ClaudeCode.Service
{
    /// <summary>
    /// Central dependency container for all Claude subsystems.
    /// Manages lifecycle of CliManager, ConversationModel, CheckpointManager, etc.
    ///
    /// Round 3: per-instance keyed by VS tool window instance ID, supporting
    /// multiple concurrent Claude windows (matches IntelliJ ClaudeChatPanel
    /// per-tab independence).
    ///
    /// Backwards-compat: GetInstance() with no arg returns the default (id=0) instance.
    /// </summary>
    public class ClaudeProjectService : IDisposable
    {
        private const int DefaultInstanceId = 0;

        private static readonly ConcurrentDictionary<int, ClaudeProjectService> _instances = new();

        public int InstanceId { get; }

        public ClaudeCliManager CliManager { get; }
        public ConversationModel ConversationModel { get; }
        public CheckpointManager CheckpointManager { get; }
        public EditDecisionManager EditDecisionManager { get; }
        public ClaudeSessionManager SessionManager { get; }
        public ClaudeStatusBar StatusBar { get; }
        public AttachmentManager AttachmentManager { get; private set; }

        private ClaudeProjectService(int instanceId, string? projectRoot)
        {
            InstanceId = instanceId;
            CliManager = new ClaudeCliManager();
            ConversationModel = new ConversationModel();
            CheckpointManager = new CheckpointManager();
            EditDecisionManager = new EditDecisionManager(CheckpointManager);
            SessionManager = new ClaudeSessionManager();
            StatusBar = new ClaudeStatusBar();
            AttachmentManager = new AttachmentManager(projectRoot);

            // Wire ConversationModel as CLI message listener
            CliManager.AddMessageListener(ConversationModel);

            // Wire StatusBar as listener to both CliManager and ConversationModel
            CliManager.AddStateListener(StatusBar);
            ConversationModel.AddListener(StatusBar);
        }

        /// <summary>Backwards-compat: get the default singleton instance (id=0).</summary>
        public static ClaudeProjectService GetInstance(string? projectRoot = null)
            => GetInstance(DefaultInstanceId, projectRoot);

        /// <summary>
        /// Get-or-create the per-instance service for a specific VS tool window instance.
        /// Round 3: each window/tab has its own CLI process, conversation model, and session manager.
        /// </summary>
        public static ClaudeProjectService GetInstance(int instanceId, string? projectRoot = null)
        {
            return _instances.GetOrAdd(instanceId, id => new ClaudeProjectService(id, projectRoot));
        }

        /// <summary>Dispose and remove a specific instance (called when its tool window closes).</summary>
        public static void RemoveInstance(int instanceId)
        {
            if (_instances.TryRemove(instanceId, out var svc))
            {
                try { svc.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Reset the default service instance (used when switching projects).
        /// Backwards-compat for callers that didn't track instance IDs.
        /// </summary>
        public static void Reset()
        {
            RemoveInstance(DefaultInstanceId);
        }

        public void Dispose()
        {
            try { CliManager.Dispose(); } catch { }
        }
    }
}
