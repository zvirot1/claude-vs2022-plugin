using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using EnvDTE80;
using CB = Microsoft.VisualStudio.CommandBars;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    // Transient = true — VS will NOT auto-restore Claude tool windows from its layout (.suo).
    // Tab persistence is handled entirely by the plugin via OpenInstanceIds settings, with a
    // hard cap of 5. Without this, VS would restore every Claude window the user ever opened
    // even though we set OpenInstanceIds = the most-recent 5.
    [ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed,
        DockedWidth = 400, DockedHeight = 600, Transient = true,
        MultiInstances = true,
        Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    [ProvideToolWindowVisibility(typeof(ClaudeToolWindow), VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string)]
    [ProvideToolWindowVisibility(typeof(ClaudeToolWindow), VSConstants.UICONTEXT.NoSolution_string)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(Settings.ClaudeOptionsPage), "Claude Code", "General", 0, 0, true)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        public const string PackageGuidString = "B1C2D3E4-F5A6-7890-BCDE-F12345678901";

        public static ClaudeCodePackage? Instance { get; private set; }
        private DTE2? _dte;

        // Prevent GC of button refs
        private CB.CommandBarButton? _openBtn, _newBtn;
        private CB.CommandBarButton? _ctxSend, _ctxExplain, _ctxReview, _ctxRefactor, _ctxAnalyze;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;

            try
            {
                _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
                if (_dte != null)
                {
                    AddToolsMenu(_dte);
                    AddEditorContextMenu(_dte);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeCodePackage] DTE init error: {ex.Message}");
            }

            // Wire VSCT command handlers so keyboard shortcuts (defined in .vsct KeyBindings) work.
            // VSCT defines: Ctrl+Shift+C → OpenClaude, Ctrl+Shift+S → SendSelection,
            //               Ctrl+Shift+R → RefactorCode, Ctrl+Escape → FocusToggle
            try
            {
                if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService cmdService)
                {
                    RegisterVsctCommand(cmdService, CommandIds.OpenClaude, OnVsctOpenClaude);
                    RegisterVsctCommand(cmdService, CommandIds.NewSession, OnVsctNewSession);
                    RegisterVsctCommand(cmdService, CommandIds.SendSelection, (_, __) => DoEditorAction(null));
                    RegisterVsctCommand(cmdService, CommandIds.ExplainCode, (_, __) => DoEditorAction("Explain"));
                    RegisterVsctCommand(cmdService, CommandIds.ReviewCode, (_, __) => DoEditorAction("Review"));
                    RegisterVsctCommand(cmdService, CommandIds.RefactorCode, (_, __) => DoEditorAction("Refactor"));
                    RegisterVsctCommand(cmdService, CommandIds.AnalyzeFile, (_, __) => DoEditorAction("Analyze"));
                    RegisterVsctCommand(cmdService, CommandIds.FocusToggle, (_, __) => ToggleFocus());
                    RegisterVsctCommand(cmdService, CommandIds.InsertAtMention, (_, __) => InsertAtMention());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeCodePackage] VSCT command wiring error: {ex.Message}");
            }

            // Programmatically assign keyboard shortcuts via DTE (overrides VS defaults).
            // This is necessary because VSCT KeyBindings are often shadowed by built-in commands
            // (e.g., Ctrl+Shift+C is used by Class View, Ctrl+Shift+S = Save All, Ctrl+Shift+R = Record Macro).
            // We use unique shortcuts that don't clash with VS built-ins:
            //   Ctrl+Alt+C → OpenClaude (Tools.OpenClaudeChat)
            //   Ctrl+Alt+S → SendSelection
            //   Ctrl+Alt+R → RefactorCode
            //   Alt+K → InsertAtMention (Milestone 4)
            try
            {
                if (_dte != null)
                    AssignKeyboardShortcuts(_dte);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeCodePackage] Keyboard shortcut assignment error: {ex.Message}");
            }

            // B3: Restore previously-open Claude tool window instances (tab persistence across restart).
            // Deferred: restoring tool windows during InitializeAsync can collide with VS's own
            // startup window-layout loading and leave VS in a half-painted state. Run after a
            // short idle delay instead. We also run a quick sync cleanup pass first to close
            // excess windows VS already auto-restored from its layout.
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                // Multiple passes — VS keeps restoring frames over the first few seconds,
                // and dehydrated/auto-hidden tabs don't all materialize at once.
                foreach (var ms in new[] { 500, 1500, 3500, 6000 })
                {
                    try
                    {
                        await Task.Delay(ms, DisposalToken);
                        var n = await CloseExcessClaudeFramesAsync(5);
                        if (n > 0) System.Diagnostics.Debug.WriteLine($"[Cap] {ms}ms-pass closed {n} excess");
                    }
                    catch { }
                }

                try
                {
                    await Task.Delay(2500, DisposalToken);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    const int RestoreCap = 5;
                    var s = Settings.ClaudeSettings.Instance;

                    // ONE-TIME CLEANUP: prune persisted state down to the cap so VS's own
                    // layout-restore can't bring back more than the user wants. We trim
                    // OpenInstanceIds + LastSessionIdPerInstance to keep only the 5 newest
                    // (largest InstanceId = most recent timestamps).
                    if (s.OpenInstanceIds != null && s.OpenInstanceIds.Count > RestoreCap)
                    {
                        var keep = s.OpenInstanceIds.Distinct().OrderBy(i => i).ToList();
                        keep = keep.GetRange(keep.Count - RestoreCap, RestoreCap);
                        var keepSet = new System.Collections.Generic.HashSet<int>(keep);
                        s.OpenInstanceIds = keep;
                        if (s.LastSessionIdPerInstance != null)
                        {
                            var stale = s.LastSessionIdPerInstance.Keys.Where(k => !keepSet.Contains(k)).ToList();
                            foreach (var k in stale) s.LastSessionIdPerInstance.Remove(k);
                        }
                        s.Save();
                    }

                    // Second pass via the Shell-level frame enumeration (catches dehydrated tabs).
                    var n2 = await CloseExcessClaudeFramesAsync(RestoreCap);
                    System.Diagnostics.Debug.WriteLine($"[Cap] second-pass closed {n2} excess Claude frames");

                    var ids = s.OpenInstanceIds;

                    // VS auto-restores tool windows from its own layout *before* this code runs.
                    // First step: if we already have more than the cap open, close the oldest
                    // Claude panels (smallest InstanceId — earlier timestamps) until we're at the cap.
                    var alreadyOpen = UI.ClaudeChatPanel.GetOpenWindowCount();
                    System.Diagnostics.Debug.WriteLine(
                        $"[Restore] persisted={ids?.Count ?? 0}, alreadyOpen={alreadyOpen}, cap={RestoreCap}");

                    if (alreadyOpen > RestoreCap)
                    {
                        var openIds = UI.ClaudeChatPanel.GetOpenInstanceIds();
                        // Keep newest (largest InstanceId — recent timestamps); close oldest.
                        openIds.Sort();
                        var toClose = openIds.Count - RestoreCap;
                        for (int i = 0; i < toClose; i++)
                        {
                            var idToClose = openIds[i];
                            try { await CloseToolWindowAsync(typeof(ClaudeToolWindow), idToClose); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Restore] close id={idToClose} failed: {ex.Message}"); }
                        }
                    }

                    if (ids == null || ids.Count == 0) return;
                    var slotsLeft = System.Math.Max(0, RestoreCap - UI.ClaudeChatPanel.GetOpenWindowCount());

                    // Capture the most-recent ids and clear the persisted list (the ctor of
                    // each re-opened window will re-add itself).
                    var distinct = ids.Distinct().ToList();
                    s.OpenInstanceIds = new System.Collections.Generic.List<int>();
                    s.Save();

                    if (slotsLeft == 0) return;

                    var toRestore = distinct.Count <= slotsLeft
                        ? distinct
                        : distinct.GetRange(distinct.Count - slotsLeft, slotsLeft);

                    foreach (var id in toRestore)
                    {
                        if (UI.ClaudeChatPanel.GetOpenWindowCount() >= RestoreCap) break;
                        try { await ShowToolWindowAsync(typeof(ClaudeToolWindow), id, true, DisposalToken); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Restore] id={id} failed: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ClaudeCodePackage] Tab restore error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Close a specific multi-instance tool window (by type+id) without disposing the package.
        /// Used by the startup cap to trim excess Claude panels VS auto-restored from its layout.
        /// </summary>
        private async Task CloseToolWindowAsync(Type toolWindowType, int instanceId)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            // FindToolWindow returns null for windows not yet shown; here we know they are
            // because GetOpenInstanceIds enumerated registered panels.
            var pane = FindToolWindow(toolWindowType, instanceId, false);
            if (pane?.Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame)
                frame.CloseFrame((uint)Microsoft.VisualStudio.Shell.Interop.__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        /// <summary>
        /// Enumerate ALL VS tool window frames matching our ClaudeToolWindow GUID — including
        /// collapsed/dehydrated ones VS restored from its layout but for which OnToolWindowCreated
        /// hasn't fired yet. This is the only reliable way to enforce the per-startup cap, because
        /// the ctor-based self-close only runs when the frame is materialized.
        /// </summary>
        private async Task<int> CloseExcessClaudeFramesAsync(int cap)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            int closed = 0;
            int totalFound = 0;
            try
            {
                var shellSvc = await GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShell));
                var shellBase = shellSvc as Microsoft.VisualStudio.Shell.Interop.IVsUIShell;
                if (shellBase == null) return 0;

                var typeGuid = typeof(ClaudeToolWindow).GUID;
                var frames = new System.Collections.Generic.List<(Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame, int order)>();

                Microsoft.VisualStudio.Shell.Interop.IEnumWindowFrames? enumerator = null;
                try
                {
                    var hr = shellBase.GetToolWindowEnum(out enumerator);
                    if (hr != Microsoft.VisualStudio.VSConstants.S_OK) enumerator = null;
                }
                catch { enumerator = null; }

                int frameCount = 0;
                if (enumerator != null)
                {
                    var arr = new Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame[1];
                    while (enumerator.Next(1, arr, out var fetched) == Microsoft.VisualStudio.VSConstants.S_OK && fetched == 1)
                    {
                        var f = arr[0]; if (f == null) continue;
                        frameCount++;
                        // Try multiple GUID properties — different VS versions / window types
                        // expose the type GUID under different VSFPROPID slots.
                        Guid? matchedGuid = null;
                        foreach (var propId in new[] { -3007, -4007, -4006, -3000, -3500 })
                        {
                            if (f.GetProperty(propId, out var p) == 0 && p is Guid gg)
                            {
                                if (gg == typeGuid) { matchedGuid = gg; break; }
                            }
                        }
                        if (matchedGuid != null)
                        {
                            totalFound++;
                            int id = 0;
                            if (f.GetProperty(-3030, out var idObj) == 0 && idObj is int n) id = n;
                            frames.Add((f, id));
                        }
                    }
                }
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCode", "diag.log"),
                    $"[{DateTime.Now:HH:mm:ss}] [Cap] enumerator={(enumerator != null)} totalFrames={frameCount} matched={totalFound}\n");

                ClaudeCode.Diagnostics.Log("Cap", $"EnumWindowFrames found {totalFound} Claude frames, cap={cap}");
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCode", "diag.log"),
                    $"[{DateTime.Now:HH:mm:ss}] [Cap] found={totalFound} cap={cap}\n");

                if (frames.Count <= cap) return 0;

                // Keep newest (highest InstanceId — recent timestamps). Close the rest.
                frames.Sort((a, b) => a.order.CompareTo(b.order));
                var toCloseCount = frames.Count - cap;
                for (int i = 0; i < toCloseCount; i++)
                {
                    try
                    {
                        // SaveAll first to avoid losing in-flight panel state, then NoSave close.
                        frames[i].frame.CloseFrame((uint)Microsoft.VisualStudio.Shell.Interop.__FRAMECLOSE.FRAMECLOSE_NoSave);
                        closed++;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CloseExcess] {ex.Message}"); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CloseExcess] {ex.Message}"); }
            return closed;
        }

        /// <summary>
        /// Programmatically assign keyboard shortcuts via DTE.Commands API.
        /// This overrides VS built-in shortcuts that may shadow our VSCT bindings.
        /// </summary>
        private void AssignKeyboardShortcuts(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Command names follow pattern: "{CmdSetGuidName}.{CommandId}"
            // VS auto-derives names from VSCT button captions, lowercased and stripped.
            // Try multiple naming schemes since VS naming is unpredictable.
            TryAssignShortcut(dte, new[] { "Tools.OpenClaudeChat", "ClaudeCode.OpenClaudeChat" }, "Global::Ctrl+Alt+C");
            TryAssignShortcut(dte, new[] { "EditorContextMenus.CodeWindow.SendSelectiontoClaude", "Tools.SendSelectiontoClaude" }, "Text Editor::Ctrl+Alt+S");
            TryAssignShortcut(dte, new[] { "EditorContextMenus.CodeWindow.RefactorCode", "Tools.RefactorCode" }, "Text Editor::Ctrl+Alt+R");
        }

        private static void TryAssignShortcut(DTE2 dte, string[] candidateNames, string binding)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var name in candidateNames)
            {
                try
                {
                    var cmd = dte.Commands.Item(name);
                    if (cmd != null)
                    {
                        cmd.Bindings = new object[] { binding };
                        return;
                    }
                }
                catch { /* command not found by this name, try next */ }
            }
        }

        private static void RegisterVsctCommand(OleMenuCommandService svc, int cmdId, EventHandler handler)
        {
            var id = new System.ComponentModel.Design.CommandID(CommandIds.CommandSetGuid, cmdId);
            var cmd = new OleMenuCommand(handler, id);
            svc.AddCommand(cmd);
        }

        private void OnVsctOpenClaude(object? sender, EventArgs e) => OpenClaudeToolWindow();
        private void OnVsctNewSession(object? sender, EventArgs e)
        {
            OpenClaudeToolWindow();
            GetClaudePanel()?.ExecuteSlashCommand("/new");
        }

        #region Tools Menu

        private void AddToolsMenu(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var bars = (CB.CommandBars)dte.CommandBars;
                var toolsPopup = ((CB.CommandBar)bars["MenuBar"]).Controls["Tools"] as CB.CommandBarPopup;
                if (toolsPopup == null) return;

                _openBtn = MakeButton(toolsPopup.Controls, "Open Claude Chat", true);
                _openBtn.Click += OnOpenClaude;

                _newBtn = MakeButton(toolsPopup.Controls, "Claude: New Conversation", false);
                _newBtn.Click += OnNewSession;
            }
            catch { }
        }

        private void OnOpenClaude(CB.CommandBarButton ctrl, ref bool cancel) => OpenClaudeToolWindow();

        private void OnNewSession(CB.CommandBarButton ctrl, ref bool cancel)
        {
            OpenClaudeToolWindow();
            GetClaudePanel()?.ExecuteSlashCommand("/new");
        }

        #endregion

        #region Editor Context Menu

        private void AddEditorContextMenu(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var bars = (CB.CommandBars)dte.CommandBars;
                CB.CommandBar? codeWin = null;
                try { codeWin = (CB.CommandBar)bars["Code Window"]; } catch { }
                if (codeWin == null) return;

                var popup = (CB.CommandBarPopup)codeWin.Controls.Add(
                    CB.MsoControlType.msoControlPopup,
                    System.Reflection.Missing.Value, System.Reflection.Missing.Value,
                    codeWin.Controls.Count + 1, true);
                popup.Caption = "Claude Code";
                popup.BeginGroup = true;

                _ctxSend = MakeButton(popup.Controls, "Send Selection to Claude", false);
                _ctxSend.Click += OnCtxSend;

                _ctxExplain = MakeButton(popup.Controls, "Explain Code", false);
                _ctxExplain.Click += OnCtxExplain;

                _ctxReview = MakeButton(popup.Controls, "Review Code", false);
                _ctxReview.Click += OnCtxReview;

                _ctxRefactor = MakeButton(popup.Controls, "Refactor Code", false);
                _ctxRefactor.Click += OnCtxRefactor;

                _ctxAnalyze = MakeButton(popup.Controls, "Analyze This File", true);
                _ctxAnalyze.Click += OnCtxAnalyze;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeCodePackage] Context menu error: {ex.Message}");
            }
        }

        private void OnCtxSend(CB.CommandBarButton c, ref bool cancel) => DoEditorAction(null);
        private void OnCtxExplain(CB.CommandBarButton c, ref bool cancel) => DoEditorAction("Explain");
        private void OnCtxReview(CB.CommandBarButton c, ref bool cancel) => DoEditorAction("Review");
        private void OnCtxRefactor(CB.CommandBarButton c, ref bool cancel) => DoEditorAction("Refactor");
        private void OnCtxAnalyze(CB.CommandBarButton c, ref bool cancel) => DoEditorAction("Analyze");

        private void DoEditorAction(string? prefix)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var sel = _dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
                var text = sel?.Text ?? "";
                var file = _dte?.ActiveDocument?.Name ?? "";

                string msg;
                if (!string.IsNullOrEmpty(prefix))
                    msg = string.IsNullOrEmpty(text)
                        ? $"{prefix} the file {file}"
                        : $"{prefix} this code from {file}:\n\n```\n{text}\n```";
                else
                    msg = string.IsNullOrEmpty(text) ? $"Here is the file {file}" : text;

                OpenClaudeToolWindow();
                GetClaudePanel()?.SendMessageFromCommand(msg);
            }
            catch { }
        }

        #endregion

        #region Alt+K @-Mention & Ctrl+Escape Toggle

        /// <summary>Insert @-mention of current file+selection into Claude input.</summary>
        public void InsertAtMention()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = _dte?.ActiveDocument;
                if (doc == null) return;
                var sel = doc.Selection as EnvDTE.TextSelection;
                string mention;
                if (sel != null && !string.IsNullOrEmpty(sel.Text))
                {
                    mention = sel.TopLine == sel.BottomLine
                        ? $"@{doc.Name}:{sel.TopLine}"
                        : $"@{doc.Name}:{sel.TopLine}-{sel.BottomLine}";
                }
                else
                {
                    mention = $"@{doc.Name}";
                }
                OpenClaudeToolWindow();
                GetClaudePanel()?.InsertTextInInput(mention);
            }
            catch { }
        }

        /// <summary>Toggle focus between editor and Claude chat (Ctrl+Escape).</summary>
        public void ToggleFocus()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var activeCaption = _dte?.ActiveWindow?.Caption ?? "";
                if (activeCaption.Contains("Claude"))
                {
                    _dte?.ActiveDocument?.Activate();
                }
                else
                {
                    OpenClaudeToolWindow();
                    GetClaudePanel()?.FocusInput();
                }
            }
            catch
            {
                OpenClaudeToolWindow();
            }
        }

        #endregion

        #region Helpers

        public void OpenClaudeToolWindow()
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, true, DisposalToken);
                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
            });
        }

        private UI.ClaudeChatPanel? GetClaudePanel()
        {
            var window = FindToolWindow(typeof(ClaudeToolWindow), 0, false);
            return (window as ClaudeToolWindow)?.Content as UI.ClaudeChatPanel;
        }

        private static CB.CommandBarButton MakeButton(CB.CommandBarControls controls, string caption, bool beginGroup)
        {
            var btn = (CB.CommandBarButton)controls.Add(
                CB.MsoControlType.msoControlButton,
                System.Reflection.Missing.Value, System.Reflection.Missing.Value,
                controls.Count + 1, true);
            btn.Caption = caption;
            btn.Style = CB.MsoButtonStyle.msoButtonCaption;
            btn.BeginGroup = beginGroup;
            return btn;
        }

        #endregion

        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType)
        {
            if (toolWindowType == typeof(ClaudeToolWindow).GUID) return this;
            return base.GetAsyncToolWindowFactory(toolWindowType);
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id)
        {
            if (toolWindowType == typeof(ClaudeToolWindow)) return "Claude Code";
            return base.GetToolWindowTitle(toolWindowType, id);
        }

        protected override Task<object?> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
            => Task.FromResult<object?>(null);
    }
}
