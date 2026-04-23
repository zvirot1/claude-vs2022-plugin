using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode
{
    /// <summary>
    /// Tool window that hosts the Claude Code chat panel with WebView2.
    ///
    /// Round 3: supports multiple instances via ToolWindowPane's built-in instance ID
    /// (passed by VS when ShowToolWindowAsync is called with different IDs).
    /// Each instance gets its own ClaudeChatPanel + per-instance ClaudeProjectService.
    /// </summary>
    [Guid("C1D2E3F4-A5B6-7890-CDEF-123456789012")]
    public class ClaudeToolWindow : ToolWindowPane
    {
        private static int _nextInstanceId = 0;
        private readonly int _myInstanceId;

        /// <summary>Set to true by HandleNewConversationWindow so the next-created tool window starts fresh (no auto-resume).</summary>
        public static volatile bool NextIsUserInitiatedFresh = false;

        private readonly string _defaultCaption;

        public ClaudeToolWindow() : base(null)
        {
            _myInstanceId = System.Threading.Interlocked.Increment(ref _nextInstanceId) - 1;
            bool isFresh = NextIsUserInitiatedFresh;
            NextIsUserInitiatedFresh = false;
            if (isFresh)
            {
                // User clicked "New Conversation Window" — ensure a clean slate by clearing
                // any stale persisted session id mapped to this instance id.
                try
                {
                    var s = Settings.ClaudeSettings.Instance;
                    if (s.LastSessionIdPerInstance != null && s.LastSessionIdPerInstance.Remove(_myInstanceId))
                        s.Save();
                }
                catch { }
            }
            _defaultCaption = _myInstanceId == 0 ? "Claude Code" : $"Claude Code ({_myInstanceId + 1})";
            // Eagerly look up this instance's persisted session summary so the tab caption
            // shows the user's chosen name even before the panel content is loaded/focused.
            string initialCaption = _defaultCaption;
            try
            {
                var s = Settings.ClaudeSettings.Instance;
                if (s.LastSessionIdPerInstance != null &&
                    s.LastSessionIdPerInstance.TryGetValue(_myInstanceId, out var sid) &&
                    !string.IsNullOrEmpty(sid))
                {
                    var store = new Session.SessionStore();
                    var info = store.LoadSession(sid);
                    if (!string.IsNullOrWhiteSpace(info?.Summary))
                        initialCaption = info!.Summary!;
                }
            }
            catch { }
            Caption = initialCaption;
            var panel = new UI.ClaudeChatPanel();
            panel.SetInstanceId(_myInstanceId);
            // Allow the panel's rename action to update this tab's caption.
            panel.CaptionUpdater = newTitle =>
            {
                try
                {
                    Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Caption = string.IsNullOrWhiteSpace(newTitle) ? _defaultCaption : newTitle!;
                    });
                }
                catch { }
            };
            Content = panel;
            // B3: remember this instance as open (persisted for next VS launch)
            try
            {
                var s = Settings.ClaudeSettings.Instance;
                s.OpenInstanceIds ??= new System.Collections.Generic.List<int>();
                if (!s.OpenInstanceIds.Contains(_myInstanceId))
                {
                    s.OpenInstanceIds.Add(_myInstanceId);
                    s.Save();
                }
            }
            catch { }
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
        }

        private int GetMultiInstanceId()
        {
            // ToolWindowPane has Frame which can give us the multi-instance ID via IVsWindowFrame property.
            // Simpler: ToolWindowPane has a public 'BitmapResourceID' but no direct InstanceID property.
            // Use Frame.GetProperty(VSFPROPID_MultiInstanceID) if available.
            try
            {
                if (Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame)
                {
                    // VSFPROPID_MultiInstanceID = -3030 (defined in vsshell.idl)
                    int VSFPROPID_MultiInstanceID = -3030;
                    if (frame.GetProperty(VSFPROPID_MultiInstanceID, out var idObj) == 0 && idObj is int id)
                        return id;
                }
            }
            catch { }
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (Content is UI.ClaudeChatPanel panel && disposing)
                {
                    var id = panel.InstanceId;
                    if (id != 0) // don't dispose the default singleton on close
                        Service.ClaudeProjectService.RemoveInstance(id);
                    UI.ClaudeChatPanel.UnregisterInstance(id, panel);
                    // B3: forget this instance from persisted open-list
                    try
                    {
                        var s = Settings.ClaudeSettings.Instance;
                        if (s.OpenInstanceIds != null && s.OpenInstanceIds.Remove(id))
                            s.Save();
                    }
                    catch { }
                }
            }
            catch { }
            base.Dispose(disposing);
        }
    }
}
