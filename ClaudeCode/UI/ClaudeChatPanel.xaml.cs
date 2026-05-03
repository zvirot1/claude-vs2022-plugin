using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCode.Cli;
using ClaudeCode.Handlers;
using ClaudeCode.Model;
using ClaudeCode.Service;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.UI
{
    /// <summary>
    /// Main chat panel hosting the WebView2 browser and wiring CLI, model, and webview together.
    /// Port of com.anthropic.claude.intellij.ui.ClaudeChatPanel (code-only, no XAML).
    /// </summary>
    public class ClaudeChatPanel : UserControl, IConversationListener, ICliStateListener
    {
        private readonly WebView2 _webView;
        private readonly TextBlock _loadingText;
        private readonly Grid _grid;

        private ClaudeProjectService? _service;
        private ClaudeCliManager? _cliManager;
        private ConversationModel? _model;
        private WebviewBridge? _bridge;
        private readonly ConcurrentDictionary<string, object> _pendingToolInputs = new ConcurrentDictionary<string, object>();
        private bool _initialized = false;

        // Round 3: per-tool-window-instance ID; default 0 (singleton).
        // Set by ClaudeToolWindow.OnToolWindowCreated() once VS provides the multi-instance ID.
        public int InstanceId { get; private set; } = 0;

        /// <summary>Callback to update the hosting ToolWindowPane's Caption (tab label). Set by ClaudeToolWindow.</summary>
        public Action<string>? CaptionUpdater { get; set; }

        public void SetInstanceId(int id)
        {
            InstanceId = id;
            // If service was already created with default ID, swap it for the per-instance one
            if (_initialized && _service != null && _service.InstanceId != id)
            {
                // Re-acquire the per-instance service
                _service = ClaudeProjectService.GetInstance(id, GetWorkingDirectory());
                _cliManager = _service.CliManager;
                _model = _service.ConversationModel;
            }
        }

        // Ensure only ONE panel instance is active globally PER tool window instance ID
        private static readonly ConcurrentDictionary<int, ClaudeChatPanel> _activeInstances = new();

        public static void UnregisterInstance(int id, ClaudeChatPanel panel)
        {
            if (_activeInstances.TryGetValue(id, out var cur) && cur == panel)
                ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<int, ClaudeChatPanel>>)_activeInstances)
                    .Remove(new System.Collections.Generic.KeyValuePair<int, ClaudeChatPanel>(id, panel));
        }

        public ClaudeChatPanel()
        {
            _grid = new Grid();

            // Detect VS theme EARLY to set correct initial colors (avoids dark flash)
            var isLight = IsVsLightTheme();

            _webView = new WebView2
            {
                DefaultBackgroundColor = isLight
                    ? System.Drawing.Color.FromArgb(255, 255, 255, 255)
                    : System.Drawing.Color.FromArgb(255, 30, 30, 30)
            };
            _grid.Children.Add(_webView);

            _loadingText = new TextBlock
            {
                Text = "Initializing Claude Code...",
                Foreground = new SolidColorBrush(isLight
                    ? System.Windows.Media.Color.FromRgb(100, 100, 100)
                    : System.Windows.Media.Color.FromRgb(128, 128, 128)),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var loadingBorder = new Border
            {
                Background = new SolidColorBrush(isLight
                    ? System.Windows.Media.Color.FromRgb(255, 255, 255)
                    : System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Child = _loadingText
            };
            _grid.Children.Add(loadingBorder);

            Content = _grid;
            Loaded += OnLoaded;

            // Store loading border ref so we can hide it
            _loadingBorder = loadingBorder;
        }

        private readonly Border _loadingBorder;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            // Round 3: per-instance gating — only one panel per InstanceId.
            // Multiple Claude tool windows (different InstanceIds) can run simultaneously.
            var existing = _activeInstances.GetOrAdd(InstanceId, this);
            if (existing != this)
            {
                DebugLog($"OnLoaded SKIP - another instance is active for InstanceId={InstanceId}");
                return;
            }
            DebugLog($"OnLoaded START (InstanceId={InstanceId})");
            try
            {
                // Per-instance WebView2 user-data folder to avoid lock conflicts across windows
                var udfRoot = Path.Combine(Path.GetTempPath(), "ClaudeCode-VS2022-WebView2");
                var udf = Path.Combine(udfRoot, $"inst-{InstanceId}");
                try { Directory.CreateDirectory(udf); } catch { }
                _webView.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
                {
                    UserDataFolder = udf
                };
                DebugLog($"EnsureCoreWebView2Async UDF={udf}");
                await _webView.EnsureCoreWebView2Async(null);
                DebugLog("WebView2 ready");

                _bridge = new WebviewBridge(_webView);
                _bridge.MessageHandler = HandleWebviewMessage;

                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var htmlPath = Path.Combine(assemblyDir, "Resources", "webview", "index.html");
                DebugLog($"HTML path: {htmlPath}, exists={File.Exists(htmlPath)}");
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                    DebugLog("Navigation started");
                }

                _webView.NavigationCompleted += (_, __) =>
                {
                    _bridge.InjectBridgeFunction();
                    _loadingBorder.Visibility = Visibility.Collapsed;
                };

                // Round 3: per-instance project service (each tool window instance has its own)
                var workingDir = GetWorkingDirectory();
                _service = ClaudeProjectService.GetInstance(InstanceId, workingDir);
                _cliManager = _service.CliManager;
                _model = _service.ConversationModel;
                // Remove first to prevent duplicates (VS may create multiple panel instances)
                _model.RemoveListener(this);
                _model.AddListener(this);
                _cliManager.RemoveStateListener(this);
                _cliManager.AddStateListener(this);

                // Detect VS theme and update webview
                DetectAndApplyTheme();
            }
            catch (Exception ex)
            {
                DebugLog($"OnLoaded ERROR: {ex.Message}\n{ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine($"[ClaudeChatPanel] Init error: {ex.Message}");
            }
        }

        #region Webview Message Handler

        private void HandleWebviewMessage(string type, string dataJson)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    switch (type)
                    {
                        case "webview_ready":
                            HandleWebviewReady();
                            break;
                        case "send_message":
                            HandleSendMessage(dataJson);
                            break;
                        case "stop_generation":
                            HandleStopGeneration();
                            break;
                        case "new_session":
                            HandleNewSession();
                            break;
                        case "accept_permission":
                            HandlePermissionResponse(dataJson, true);
                            break;
                        case "reject_permission":
                            HandlePermissionResponse(dataJson, false);
                            break;
                        case "always_allow_permission":
                            HandlePermissionResponse(dataJson, true);
                            break;
                        case "execute_slash_command":
                            HandleSlashCommand(dataJson);
                            break;
                        case "reconnect":
                            HandleReconnect();
                            break;
                        case "file_search":
                            HandleFileSearch(dataJson);
                            break;
                        case "resume_session":
                            var resumeData = JObject.Parse(dataJson);
                            HandleResume(resumeData.Value<string>("sessionId"));
                            break;
                        case "clear_conversation":
                            _model?.Clear();
                            _bridge?.SendToWebview("conversation_cleared", "{}");
                            break;
                        case "open_dialog":
                            HandleOpenDialog(dataJson);
                            break;
                        case "apply_to_editor":
                            HandleApplyToEditor(dataJson);
                            break;
                        case "insert_at_cursor":
                            HandleInsertAtCursor(dataJson);
                            break;
                        case "new_tab":
                            HandleNewConversationWindow();
                            break;
                        case "fork_from_message":
                            HandleForkFromMessage(dataJson);
                            break;
                        case "remove_attachment":
                            HandleRemoveAttachment(dataJson);
                            break;
                        case "attach_file_dialog":
                            HandleAttachFileDialog();
                            break;
                        case "change_mode":
                            HandleChangeMode(dataJson);
                            break;
                        case "change_effort":
                            HandleChangeEffort(dataJson);
                            break;
                        case "change_model":
                            HandleChangeModel(dataJson);
                            break;
                        case "set_attach_active_file":
                            HandleSetAttachActiveFile(dataJson);
                            break;
                        case "rename_session":
                            HandleRenameSession(dataJson);
                            break;
                        case "view_diff":
                            HandleViewDiff(dataJson);
                            break;
                        case "accept_edit":
                            HandleAcceptEdit(dataJson);
                            break;
                        case "reject_edit":
                            HandleRejectEdit(dataJson);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ClaudeChatPanel] Message handler error: {ex.Message}");
                }
            });
        }

        private bool _webviewReadyHandled = false;
        private void HandleWebviewReady()
        {
            // Only handle once (webview_ready can fire multiple times from duplicate panels)
            if (_webviewReadyHandled) return;
            _webviewReadyHandled = true;

            var settings = Settings.ClaudeSettings.Instance;
            // Send enter mode preference (JS: set_enter_mode with ctrlEnter flag)
            _bridge?.SendToWebview("set_enter_mode", JsonConvert.SerializeObject(new
            {
                ctrlEnter = settings.UseCtrlEnterToSend
            }));
            // Detect VS theme and send to webview
            var detectedTheme = DetectVsTheme();
            _bridge?.SendToWebview("set_theme", JsonConvert.SerializeObject(new
            {
                theme = detectedTheme
            }));
            // Send initial permission mode to webview (Milestone 3)
            var initialMode = string.IsNullOrEmpty(settings.InitialPermissionMode) ? "default" : settings.InitialPermissionMode;
            _bridge?.SendToWebview("mode_changed", JsonConvert.SerializeObject(new { mode = initialMode }));
            // Round 3: send initial effort level
            var initialEffort = string.IsNullOrEmpty(settings.EffortLevel) ? "auto" : settings.EffortLevel;
            _bridge?.SendToWebview("effort_changed", JsonConvert.SerializeObject(new { effort = initialEffort }));
            // IntelliJ Round 7: send Active-file toggle state + start watching for editor switches
            _bridge?.SendToWebview("attach_active_file_changed",
                JsonConvert.SerializeObject(new { enabled = settings.AttachActiveFile }));
            InitActiveFileListener();
            // Only start CLI if not already running OR starting (Eclipse fix #10: avoid races
            // where webview_ready fires twice during slow startup → duplicate CLI processes).
            if (_cliManager == null || !_cliManager.IsRunningOrStarting)
                StartCli();
        }

        /// <summary>
        /// Handle change_mode message from webview: stop current CLI, restart with new permission mode,
        /// preserving session via --resume.
        /// </summary>
        private void HandleChangeMode(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var newMode = data.Value<string>("mode") ?? "default";

                // Persist preference
                var settings = Settings.ClaudeSettings.Instance;
                settings.InitialPermissionMode = newMode;
                settings.Save();

                // Capture current session ID (for --resume on restart)
                var sessionId = _model?.SessionInfo?.SessionId;

                // Restart CLI with new mode
                if (_cliManager != null && _cliManager.IsRunning)
                {
                    var cliPath = Cli.ClaudeCliManager.GetCliPath();
                    if (!string.IsNullOrEmpty(cliPath))
                    {
                        var workingDir = GetWorkingDirectory();
                        var newConfig = new Cli.CliProcessConfig(cliPath!, workingDir)
                        {
                            PermissionMode = newMode == "default" ? null : newMode,
                            Model = settings.SelectedModel != "default" ? settings.SelectedModel : null,
                            ResumeSessionId = sessionId
                        };
                        _cliManager.Stop();
                        _cliManager.Start(newConfig);
                    }
                }

                // Confirm to webview
                _bridge?.SendToWebview("mode_changed", JsonConvert.SerializeObject(new { mode = newMode }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeChatPanel] HandleChangeMode error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle change_effort: persist new effort level, hot-swap CLI with --effort flag and --resume.
        /// Round 3.
        /// </summary>
        private void HandleChangeEffort(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var newEffort = data.Value<string>("effort") ?? "auto";

                var settings = Settings.ClaudeSettings.Instance;
                settings.EffortLevel = newEffort;
                settings.Save();

                // Hot-swap CLI to apply effort change (preserves session via --resume)
                var sessionId = _model?.SessionInfo?.SessionId;
                if (_cliManager != null && _cliManager.IsRunning)
                {
                    var cliPath = Cli.ClaudeCliManager.GetCliPath();
                    if (!string.IsNullOrEmpty(cliPath))
                    {
                        var workingDir = GetWorkingDirectory();
                        var newConfig = new Cli.CliProcessConfig(cliPath!, workingDir)
                        {
                            PermissionMode = settings.InitialPermissionMode == "default" ? null : settings.InitialPermissionMode,
                            Model = settings.SelectedModel != "default" ? settings.SelectedModel : null,
                            Effort = newEffort == "auto" ? null : newEffort,
                            ResumeSessionId = sessionId
                        };
                        _cliManager.Stop();
                        _cliManager.Start(newConfig);
                    }
                }

                _bridge?.SendToWebview("effort_changed", JsonConvert.SerializeObject(new { effort = newEffort }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeChatPanel] HandleChangeEffort error: {ex.Message}");
            }
        }

        /// <summary>C1: change_model from webview — persist model, remember custom names, hot-swap CLI.</summary>
        private void HandleChangeModel(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var newModel = (data.Value<string>("model") ?? "default").Trim();
                if (newModel.Length == 0) newModel = "default";

                var settings = Settings.ClaudeSettings.Instance;
                settings.SelectedModel = newModel;
                // Track custom (non-preset) models so we can expose them later in the prompt
                var presets = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                    { "default", "sonnet", "opus", "haiku" };
                if (!presets.Contains(newModel) && !newModel.StartsWith("claude-", System.StringComparison.OrdinalIgnoreCase)
                    && !settings.CustomModels.Contains(newModel))
                {
                    settings.CustomModels.Add(newModel);
                }
                else if (newModel.StartsWith("claude-", System.StringComparison.OrdinalIgnoreCase)
                         && !settings.CustomModels.Contains(newModel))
                {
                    settings.CustomModels.Add(newModel);
                }
                settings.Save();

                // Hot-swap CLI so the new model takes effect immediately (preserves session via --resume)
                var sessionId = _model?.SessionInfo?.SessionId;
                if (_cliManager != null && _cliManager.IsRunning)
                {
                    var cliPath = Cli.ClaudeCliManager.GetCliPath();
                    if (!string.IsNullOrEmpty(cliPath))
                    {
                        var workingDir = GetWorkingDirectory();
                        var newConfig = new Cli.CliProcessConfig(cliPath!, workingDir)
                        {
                            PermissionMode = settings.InitialPermissionMode == "default" ? null : settings.InitialPermissionMode,
                            Model = newModel != "default" ? newModel : null,
                            Effort = settings.EffortLevel == "auto" ? null : settings.EffortLevel,
                            ResumeSessionId = sessionId
                        };
                        _cliManager.Stop();
                        _cliManager.Start(newConfig);
                    }
                }

                _bridge?.SendToWebview("model_changed", JsonConvert.SerializeObject(new { model = newModel }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeChatPanel] HandleChangeModel error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle rename_session from webview (editable header title in panel).
        /// Persists summary via SessionManager.RenameSession; updates in-memory copy.
        /// Round 3.
        /// </summary>
        private void HandleRenameSession(string dataJson)
        {
            try
            {
                DebugLog($"HandleRenameSession dataJson={dataJson}");
                var data = JObject.Parse(dataJson);
                var sessionId = data.Value<string>("sessionId");
                var newName = data.Value<string>("newName")?.Trim();
                DebugLog($"  sessionId='{sessionId}' newName='{newName}'");
                // Fallback: if webview didn't know the session id (race on auto-resume),
                // use whatever the model/service currently tracks.
                if (string.IsNullOrEmpty(sessionId))
                    sessionId = _model?.SessionInfo?.SessionId
                        ?? _service?.SessionManager.CurrentSession?.SessionId;
                if (string.IsNullOrEmpty(newName))
                {
                    DebugLog("  skipping: missing newName");
                    return;
                }
                if (string.IsNullOrEmpty(sessionId))
                {
                    DebugLog("  skipping: no session id available anywhere");
                    return;
                }

                var ok = _service?.SessionManager.RenameSession(sessionId!, newName!) ?? false;
                DebugLog($"  RenameSession returned {ok}");

                // Update in-memory model copy
                if (_model?.SessionInfo != null && _model.SessionInfo.SessionId == sessionId)
                    _model.SessionInfo.Summary = newName;

                // Sync the tool-window tab caption with the renamed title
                try { CaptionUpdater?.Invoke(newName!); } catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeChatPanel] HandleRenameSession error: {ex.Message}");
            }
        }

        /// <summary>
        /// Open a new VS Tool Window instance of Claude Code, matching IntelliJ "New Tab" behavior.
        /// Each instance has its own ClaudeChatPanel + per-instance ClaudeProjectService
        /// (independent CLI process and conversation). Round 3.
        /// </summary>
        private void HandleNewConversationWindow()
        {
            var package = ClaudeCodePackage.Instance;
            if (package == null) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    // Use unique instance ID derived from timestamp (lower 31 bits to fit int)
                    var instanceId = (int)(DateTime.Now.Ticks & 0x7FFFFFFF);
                    // Signal the ClaudeToolWindow ctor that this creation is user-initiated fresh,
                    // so it clears any stale persisted session id for the assigned slot.
                    ClaudeToolWindow.NextIsUserInitiatedFresh = true;
                    var window = await package.ShowToolWindowAsync(
                        typeof(ClaudeToolWindow), instanceId, true, package.DisposalToken);
                    if (window?.Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame)
                        frame.Show();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HandleNewConversationWindow] {ex.Message}");
                }
            });
        }

        private void HandleSendMessage(string dataJson)
        {
            var data = JObject.Parse(dataJson);
            var text = data.Value<string>("message") ?? data.Value<string>("text");
            if (string.IsNullOrWhiteSpace(text)) return;

            // Check if it's a slash command (e.g. "/model sonnet" sent via command picker)
            if (SlashCommandHandler.IsSlashCommand(text) && SlashCommandHandler.IsLocalCommand(text))
            {
                var cmdName = SlashCommandHandler.GetCommandName(text);
                var cmdArgs = SlashCommandHandler.GetCommandArgs(text);
                HandleSlashCommand(JsonConvert.SerializeObject(new { command = cmdName, args = cmdArgs }));
                return;
            }

            // Handle attached images
            // JS sends images as array of base64 strings (raw, no data URL prefix)
            // OR as objects with { dataUrl, bytes } properties
            var images = data["images"] as JArray;
            if (images != null && images.Count > 0)
            {
                var imageDataList = new System.Collections.Generic.List<byte[]>();
                foreach (var img in images)
                {
                    var raw = img.ToString().Trim();
                    // Strip data URL prefix if present (e.g., "data:image/png;base64,...")
                    if (raw.Contains(","))
                        raw = raw.Substring(raw.IndexOf(',') + 1);
                    // Remove surrounding quotes if present
                    raw = raw.Trim('"');
                    try
                    {
                        if (!string.IsNullOrEmpty(raw))
                            imageDataList.Add(Convert.FromBase64String(raw));
                    }
                    catch { }
                }
                if (imageDataList.Count > 0)
                {
                    _model?.AddUserMessage(text);
                    _cliManager?.SendRichMessage(text, imageDataList);
                    return;
                }
            }

            // IntelliJ Round 7: prepend the active editor file if user enabled "Attach active file"
            if (Settings.ClaudeSettings.Instance.AttachActiveFile)
            {
                var activeCtx = BuildActiveFileContext();
                if (!string.IsNullOrEmpty(activeCtx))
                    text = activeCtx + "\n" + text;
            }

            // Handle file attachments context
            var attachmentCtx = _service?.AttachmentManager.BuildFileContext();
            if (!string.IsNullOrEmpty(attachmentCtx))
            {
                text = text + "\n\n" + attachmentCtx;
                _service?.AttachmentManager.ClearAttachments();
            }

            _model?.AddUserMessage(text);
            _cliManager?.SendMessage(text);
        }

        // ==================== Active File Pin (Amazon Q parity, IntelliJ Round 7) ====================

        private EnvDTE.WindowEvents? _windowEvents;
        private string? _lastActiveFilePath;

        /// <summary>Subscribe to DTE WindowEvents so we can push the currently-focused
        /// file to the webview as the user switches editor tabs.</summary>
        private void InitActiveFileListener()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (ClaudeCodePackage.Instance == null) return;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await ClaudeCodePackage.Instance.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                    if (dte == null) return;
                    _windowEvents = dte.Events.WindowEvents;
                    _windowEvents.WindowActivated += OnDteWindowActivated;
                    // Push the current active file once on startup
                    SendActiveFileToWebview(GetCurrentActiveFile(dte));
                });
            }
            catch (Exception ex) { DebugLog($"InitActiveFileListener error: {ex.Message}"); }
        }

        private void OnDteWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (gotFocus?.Document == null) return;
                SendActiveFileToWebview(gotFocus.Document.FullName);
            }
            catch { }
        }

        private string? GetCurrentActiveFile(EnvDTE80.DTE2 dte)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return dte.ActiveDocument?.FullName;
            }
            catch { return null; }
        }

        private void SendActiveFileToWebview(string? path)
        {
            _lastActiveFilePath = path;
            if (string.IsNullOrEmpty(path))
            {
                _bridge?.SendToWebview("active_file_changed", "{\"path\":null,\"name\":null}");
                return;
            }
            var name = System.IO.Path.GetFileName(path);
            _bridge?.SendToWebview("active_file_changed",
                JsonConvert.SerializeObject(new { path = path, name = name }));
        }

        /// <summary>Build &lt;file path="rel"&gt;contents&lt;/file&gt; for the active editor file.</summary>
        private string BuildActiveFileContext()
        {
            try
            {
                var path = _lastActiveFilePath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
                var content = System.IO.File.ReadAllText(path);
                var rel = path!;
                var cwd = GetWorkingDirectory();
                if (!string.IsNullOrEmpty(cwd) && rel.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(cwd.Length).TrimStart('\\', '/');
                return $"<file path=\"{rel.Replace("\"", "&quot;")}\">\n{content}\n</file>\n";
            }
            catch (Exception ex)
            {
                DebugLog($"BuildActiveFileContext failed: {ex.Message}");
                return "";
            }
        }

        private void HandleSetAttachActiveFile(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var enabled = data.Value<bool?>("enabled") ?? false;
                var settings = Settings.ClaudeSettings.Instance;
                settings.AttachActiveFile = enabled;
                settings.Save();
            }
            catch { }
        }

        private void HandleNewSession()
        {
            _model?.Clear();
            _cliManager?.Stop();
            _service?.CheckpointManager.ClearCheckpoints();
            _service?.EditDecisionManager.Clear();
            _service?.SessionManager.StartNewSession(GetWorkingDirectory());
            _pendingToolInputs.Clear();
            // Clear persisted auto-resume id for this instance so StartCli below
            // starts a FRESH session (otherwise B1 would silently --resume the old one).
            try
            {
                var s = Settings.ClaudeSettings.Instance;
                if (s.LastSessionIdPerInstance != null && s.LastSessionIdPerInstance.Remove(InstanceId))
                    s.Save();
            }
            catch { }
            // Tell the webview to clear the message list + show welcome again.
            _bridge?.SendToWebview("conversation_cleared", "{}");
            // Reset tab caption to default until the new session gets a rename.
            try { CaptionUpdater?.Invoke(""); } catch { }
            StartCli();
        }

        /// <summary>
        /// Stop the current CLI query but preserve conversation history by resuming the session.
        /// Port from Eclipse Phase 5: CLI is restarted with <c>--resume &lt;sessionId&gt;</c>.
        /// </summary>
        private void HandleStopGeneration()
        {
            // Fix #1: Cancel stream-inactivity watchdog so we don't show a misleading
            // "Stream timeout — CLI may be stuck" error after the user intentionally stopped.
            try { _model?.CancelStreamingTimeout(); } catch { }

            var sessionId = _model?.SessionInfo?.SessionId;
            if (!string.IsNullOrEmpty(sessionId))
            {
                // Restart CLI with --resume to preserve history
                try
                {
                    _cliManager?.InterruptCurrentQuery(sessionId);
                }
                catch
                {
                    // Fallback to plain stop if resume fails
                    _cliManager?.Stop();
                }
            }
            else
            {
                _cliManager?.Stop();
            }
        }

        private void HandlePermissionResponse(string dataJson, bool allow)
        {
            var data = JObject.Parse(dataJson);
            var requestId = data.Value<string>("requestId");
            var toolUseId = data.Value<string>("toolUseId");

            if (requestId != null)
            {
                // Retrieve the stored tool input for control_response
                _pendingToolInputs.TryRemove(requestId, out var toolInput);
                _cliManager?.SendControlResponse(requestId, allow, toolInput);
            }
            else if (toolUseId != null)
            {
                _cliManager?.SendPermissionResponse(toolUseId, allow);
            }
        }

        private void HandleSlashCommand(string dataJson)
        {
            var data = JObject.Parse(dataJson);
            var command = data.Value<string>("command");
            var args = data.Value<string>("args");

            switch (command)
            {
                case "/new":
                    HandleNewSession();
                    break;
                case "/clear":
                    _model?.Clear();
                    _bridge?.SendToWebview("conversation_cleared", "{}");
                    break;
                case "/cost":
                    if (_model?.CumulativeUsage != null)
                    {
                        var usage = _model.CumulativeUsage;
                        var costText = SlashCommandHandler.FormatCost(
                            usage.FormatTokens(), usage.FormatCost(),
                            usage.FormatDuration(), usage.TotalTurns);
                        _bridge?.SendToWebview("system_message", JsonConvert.SerializeObject(new { text = costText }));
                    }
                    break;
                case "/help":
                    var helpText = SlashCommandHandler.FormatHelp();
                    _bridge?.SendToWebview("system_message", JsonConvert.SerializeObject(new { text = helpText }));
                    break;
                case "/model":
                    if (!string.IsNullOrEmpty(args))
                    {
                        Settings.ClaudeSettings.Instance.SelectedModel = args;
                        Settings.ClaudeSettings.Instance.Save();
                        HandleNewSession();
                    }
                    break;
                case "/stop":
                    _cliManager?.Stop();
                    break;
                case "/compact":
                    _model?.AddUserMessage("/compact");
                    _cliManager?.SendMessage("/compact");
                    break;
                case "/resume":
                    HandleResume(args);
                    break;
                case "/history":
                    HandleHistory();
                    break;
                default:
                    // CLI-forwarded commands
                    if (command != null && !SlashCommandHandler.IsLocalCommand(command))
                    {
                        var fullText = args != null ? $"{command} {args}" : command;
                        _model?.AddUserMessage(fullText);
                        _cliManager?.SendMessage(fullText);
                    }
                    break;
            }
        }

        private void HandleResume(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                // Resume most recent session
                var sessions = _service?.SessionManager.ListSessions();
                if (sessions != null && sessions.Count > 0)
                {
                    sessionId = sessions[0].SessionId;
                }
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                _model?.Clear();
                _cliManager?.Stop();
                _service?.CheckpointManager.ClearCheckpoints();

                var cliPath = ClaudeCliManager.GetCliPath();
                if (cliPath == null) return;

                var config = new CliProcessConfig(cliPath, GetWorkingDirectory())
                {
                    ResumeSessionId = sessionId
                };
                try { _cliManager?.Start(config); } catch { }
            }
        }

        private void HandleHistory()
        {
            if (_service?.SessionManager == null) return;
            var sessions = _service.SessionManager.ListSessions();
            if (sessions == null || sessions.Count == 0)
            {
                _bridge?.SendToWebview("system_message", JsonConvert.SerializeObject(new
                {
                    text = "No saved sessions found."
                }));
                return;
            }

            // New IntelliJ-style dialog with 4-col table, preview pane, rename/delete/refresh
            var dlg = new Dialogs.SessionHistoryDialog(_service.SessionManager)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedSessionId))
            {
                HandleResume(dlg.SelectedSessionId);
            }
        }

        private void HandleFileSearch(string dataJson)
        {
            // Search project files matching a query (for @-mentions)
            var data = JObject.Parse(dataJson);
            var query = data.Value<string>("query") ?? "";
            if (string.IsNullOrEmpty(query) || query.Length < 2) return;

            try
            {
                var workingDir = GetWorkingDirectory();
                var results = new JArray();
                var files = Directory.GetFiles(workingDir, "*" + query + "*",
                    SearchOption.AllDirectories);
                var count = 0;
                foreach (var file in files)
                {
                    if (count >= 20) break;
                    // Skip hidden/build directories
                    var relative = file.Substring(workingDir.Length).TrimStart('\\', '/');
                    if (relative.StartsWith(".") || relative.Contains("\\.") ||
                        relative.Contains("\\bin\\") || relative.Contains("\\obj\\") ||
                        relative.Contains("\\node_modules\\")) continue;

                    results.Add(new JObject
                    {
                        ["path"] = relative,
                        ["name"] = Path.GetFileName(file)
                    });
                    count++;
                }
                _bridge?.SendToWebview("file_suggestions", JsonConvert.SerializeObject(new { files = results }));
            }
            catch { }
        }

        private void HandleOpenDialog(string dataJson)
        {
            var data = JObject.Parse(dataJson);
            var dialog = data.Value<string>("dialog");
            switch (dialog)
            {
                case "preferences":
                    ShowPreferencesDialog();
                    break;
                case "history":
                    HandleHistory();
                    break;
                case "rules":
                    new Dialogs.RulesDialog(GetWorkingDirectory()) { Owner = Window.GetWindow(this) }.ShowDialog();
                    break;
                case "mcp":
                    new Dialogs.McpServersDialog(GetWorkingDirectory()) { Owner = Window.GetWindow(this) }.ShowDialog();
                    break;
                case "hooks":
                    new Dialogs.HooksDialog(GetWorkingDirectory()) { Owner = Window.GetWindow(this) }.ShowDialog();
                    break;
                case "memory":
                    new Dialogs.MemoryDialog(GetWorkingDirectory()) { Owner = Window.GetWindow(this) }.ShowDialog();
                    break;
                case "skills":
                    new Dialogs.SkillsDialog(GetWorkingDirectory()) { Owner = Window.GetWindow(this) }.ShowDialog();
                    break;
            }
        }

        private void HandleApplyToEditor(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var code = data.Value<string>("code");
                if (string.IsNullOrEmpty(code)) return;

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var doc = dte?.ActiveDocument;
                if (doc == null) return;

                var textDoc = doc.Object("TextDocument") as EnvDTE.TextDocument;
                if (textDoc == null) return;

                // Replace selection or entire document
                var selection = textDoc.Selection;
                if (selection != null && !string.IsNullOrEmpty(selection.Text))
                {
                    selection.Insert(code);
                }
                else
                {
                    // Replace entire document content
                    var editPoint = textDoc.StartPoint.CreateEditPoint();
                    editPoint.ReplaceText(textDoc.EndPoint, code, 0);
                }
            }
            catch { }
        }

        private void HandleInsertAtCursor(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var code = data.Value<string>("code");
                if (string.IsNullOrEmpty(code)) return;

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var doc = dte?.ActiveDocument;
                var textDoc = doc?.Object("TextDocument") as EnvDTE.TextDocument;
                textDoc?.Selection?.Insert(code);
            }
            catch { }
        }

        // ==================== Edit Decisions: View Diff / Accept / Reject ====================

        /// <summary>
        /// Open VS native side-by-side diff viewer comparing original (from checkpoint) vs modified.
        /// Uses IVsDifferenceService — gets syntax highlighting for free.
        /// </summary>
        private void HandleViewDiff(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var editId = data.Value<string>("editId");
                if (string.IsNullOrEmpty(editId) || _service == null) return;

                var edit = _service.EditDecisionManager.GetEdit(editId!);
                if (edit == null) return;

                ShowVsDiffViewer(edit.FilePath, edit.OriginalContent ?? "", edit.ModifiedContent ?? "");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HandleViewDiff] {ex.Message}");
            }
        }

        private void HandleAcceptEdit(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var editId = data.Value<string>("editId");
                if (string.IsNullOrEmpty(editId) || _service == null) return;
                var edit = _service.EditDecisionManager.GetEdit(editId!);
                if (edit != null) _service.EditDecisionManager.AcceptEdit(edit.FilePath);
            }
            catch { }
        }

        private void HandleRejectEdit(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson);
                var editId = data.Value<string>("editId");
                if (string.IsNullOrEmpty(editId) || _service == null) return;
                var edit = _service.EditDecisionManager.GetEdit(editId!);
                if (edit != null) _service.EditDecisionManager.RejectEdit(edit.FilePath);
            }
            catch { }
        }

        /// <summary>
        /// Open VS native diff viewer (IVsDifferenceService) for side-by-side comparison.
        /// Writes original/modified to temp files and opens in VS comparison window.
        /// </summary>
        private void ShowVsDiffViewer(string filePath, string originalText, string modifiedText)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var fileName = System.IO.Path.GetFileName(filePath);
                    var ext = System.IO.Path.GetExtension(filePath);
                    var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeCodeDiff");
                    System.IO.Directory.CreateDirectory(tempDir);
                    var leftPath = System.IO.Path.Combine(tempDir, $"original_{fileName}");
                    var rightPath = System.IO.Path.Combine(tempDir, $"modified_{fileName}");
                    System.IO.File.WriteAllText(leftPath, originalText);
                    System.IO.File.WriteAllText(rightPath, modifiedText);

                    var diffSvc = (Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService?)
                        Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService));
                    if (diffSvc == null) return;

                    var caption = $"Claude Diff: {fileName}";
                    diffSvc.OpenComparisonWindow2(
                        leftPath,                                              // leftFileMoniker
                        rightPath,                                             // rightFileMoniker
                        caption,                                               // caption
                        $"Original {fileName} ↔ Modified {fileName} (Claude)", // Tooltip
                        $"Original — {fileName}",                              // leftLabel
                        $"Modified by Claude — {fileName}",                    // rightLabel
                        $"Claude edit: {fileName}",                            // inlineLabel
                        "",                                                    // roles
                        (uint)Microsoft.VisualStudio.Shell.Interop.__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary
                        | (uint)Microsoft.VisualStudio.Shell.Interop.__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ShowVsDiffViewer] {ex.Message}");
                }
            });
        }

        private void HandleForkFromMessage(string dataJson)
        {
            var data = JObject.Parse(dataJson);
            var messageIndex = data.Value<int?>("messageIndex") ?? -1;
            if (messageIndex < 0) return;

            // Get messages up to the fork point, start a new CLI session with --continue
            var messages = _model?.GetMessages();
            if (messages == null || messageIndex >= messages.Count) return;

            // Clear and restart with the session continued
            _cliManager?.Stop();
            var cliPath = ClaudeCliManager.GetCliPath();
            if (cliPath == null) return;

            var config = new CliProcessConfig(cliPath, GetWorkingDirectory())
            {
                ContinueSession = true,
                SessionId = _service?.SessionManager.CurrentSession?.SessionId
            };
            try { _cliManager?.Start(config); } catch { }
        }

        private void HandleRemoveAttachment(string dataJson)
        {
            var data = JObject.Parse(dataJson);
            var index = data.Value<int?>("index") ?? -1;
            _service?.AttachmentManager.RemoveAttachment(index);

            // Send updated attachments list to webview
            var attachments = _service?.AttachmentManager.GetAttachments();
            if (attachments != null)
            {
                var items = new JArray();
                foreach (var att in attachments)
                    items.Add(new JObject { ["label"] = att.GetLabel(), ["path"] = att.FilePath });
                _bridge?.SendToWebview("attachments_updated", items.ToString(Formatting.None));
            }
        }

        private void HandleAttachFileDialog()
        {
            try
            {
                // Use VS file open dialog
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Attach File to Claude",
                    Multiselect = true,
                    Filter = "All Files (*.*)|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    foreach (var file in dialog.FileNames)
                    {
                        _service?.AttachmentManager.AttachFile(file);
                    }

                    // Send updated attachments
                    var attachments = _service?.AttachmentManager.GetAttachments();
                    if (attachments != null)
                    {
                        var items = new JArray();
                        foreach (var att in attachments)
                            items.Add(new JObject { ["label"] = att.GetLabel(), ["path"] = att.FilePath });
                        _bridge?.SendToWebview("attachments_updated", items.ToString(Formatting.None));
                    }
                }
            }
            catch { }
        }

        private void ShowPreferencesDialog()
        {
            var settings = Settings.ClaudeSettings.Instance;

            // Read current CLI path from the path file
            var assemblyDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            var pathFile = System.IO.Path.Combine(assemblyDir, "claude_cli_path.txt");
            var currentCliPath = "";
            try
            {
                if (File.Exists(pathFile))
                {
                    var raw = File.ReadAllText(pathFile).Trim();
                    currentCliPath = System.IO.Path.IsPathRooted(raw) ? raw : System.IO.Path.Combine(assemblyDir, raw);
                }
            }
            catch { }

            // Build WPF dialog
            var dlg = new Window
            {
                Title = "Claude Code Preferences",
                Width = 500,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240))
            };

            var stack = new StackPanel { Margin = new Thickness(15) };

            // CLI Path
            stack.Children.Add(new TextBlock { Text = "CLI Path (claude.exe location):", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
            var cliPathBox = new TextBox { Text = currentCliPath, Margin = new Thickness(0, 0, 0, 5) };
            stack.Children.Add(cliPathBox);
            var browseBtn = new Button { Content = "Browse...", Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 15) };
            browseBtn.Click += (_, __) =>
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Claude CLI",
                    Filter = "Executables (*.exe;*.cmd)|*.exe;*.cmd|All Files (*.*)|*.*"
                };
                if (ofd.ShowDialog() == true)
                    cliPathBox.Text = ofd.FileName;
            };
            stack.Children.Add(browseBtn);

            // Model (editable ComboBox — user can pick preset or type a new model name)
            stack.Children.Add(new TextBlock { Text = "Model:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
            var modelBox = new ComboBox { IsEditable = true, Margin = new Thickness(0, 0, 0, 3) };
            modelBox.Items.Add("default");
            modelBox.Items.Add("sonnet");
            modelBox.Items.Add("opus");
            modelBox.Items.Add("haiku");
            modelBox.Items.Add("claude-sonnet-4-20250514");
            modelBox.Items.Add("claude-opus-4-20250514");
            modelBox.Items.Add("claude-sonnet-4-5-20250514");
            var currentModel = string.IsNullOrEmpty(settings.SelectedModel) ? "default" : settings.SelectedModel;
            // If the saved model isn't in the list, add it
            if (!modelBox.Items.Contains(currentModel))
                modelBox.Items.Add(currentModel);
            modelBox.Text = currentModel;
            stack.Children.Add(new TextBlock
            {
                Text = "Pick a preset or type any model name (e.g. claude-opus-4-20250514)",
                FontSize = 11,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 120, 120)),
                Margin = new Thickness(0, 0, 0, 15)
            });
            stack.Children.Add(modelBox);

            // Ctrl+Enter
            var ctrlEnterCheck = new CheckBox
            {
                Content = "Use Ctrl+Enter to send (Enter for newline)",
                IsChecked = settings.UseCtrlEnterToSend,
                Margin = new Thickness(0, 0, 0, 15)
            };
            stack.Children.Add(ctrlEnterCheck);

            // Buttons
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            stack.Children.Add(btnPanel);

            dlg.Content = stack;

            okBtn.Click += (_, __) =>
            {
                // Save CLI path
                var newCliPath = cliPathBox.Text.Trim();
                if (!string.IsNullOrEmpty(newCliPath))
                {
                    try { File.WriteAllText(pathFile, newCliPath); } catch { }
                    settings.CliPath = newCliPath;
                }

                // Save model (use .Text to support custom typed values)
                var modelText = modelBox.Text?.Trim();
                settings.SelectedModel = string.IsNullOrEmpty(modelText) ? "default" : modelText;

                // Save ctrl+enter
                settings.UseCtrlEnterToSend = ctrlEnterCheck.IsChecked == true;
                _bridge?.SendToWebview("set_enter_mode", JsonConvert.SerializeObject(new
                {
                    ctrlEnter = settings.UseCtrlEnterToSend
                }));

                settings.Save();
                dlg.DialogResult = true;
                dlg.Close();
            };

            dlg.ShowDialog();
        }

        private void HandleReconnect()
        {
            _cliManager?.Stop();
            StartCli();
        }

        private void StartCli()
        {
            try
            {
                DebugLog("StartCli called");
                var cliPath = ClaudeCliManager.GetCliPath();
                DebugLog($"GetCliPath returned: '{cliPath}'");
                if (string.IsNullOrEmpty(cliPath))
                {
                    // Show DETAILED error so we can debug in the panel itself
                    var settingsPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClaudeCode", "settings.json");
                    var settingsExists = File.Exists(settingsPath);
                    var configured = Settings.ClaudeSettings.Instance.CliPath;
                    var lastKnown = Settings.ClaudeSettings.Instance.LastKnownCliPath;
                    var npmCmd = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "Roaming", "npm", "claude.cmd");
                    var npmExists = File.Exists(npmCmd);

                    var debugMsg = $"CLI not found.\n" +
                        $"Settings: {settingsPath} (exists={settingsExists})\n" +
                        $"CliPath='{configured}' exists={(!string.IsNullOrEmpty(configured) ? File.Exists(configured).ToString() : "empty")}\n" +
                        $"LastKnown='{lastKnown}'\n" +
                        $"npm claude.cmd='{npmCmd}' exists={npmExists}\n" +
                        $"Install: npm install -g @anthropic-ai/claude-code";

                    DebugLog("CLI path is null/empty - showing detailed error");
                    _bridge?.SendToWebview("error", JsonConvert.SerializeObject(new
                    {
                        message = debugMsg
                    }));
                    return;
                }

                var workingDir = GetWorkingDirectory();
                DebugLog($"WorkingDir: '{workingDir}'");
                var settings = Settings.ClaudeSettings.Instance;

                var config = new CliProcessConfig(cliPath!, workingDir)
                {
                    PermissionMode = settings.InitialPermissionMode != "default" ? settings.InitialPermissionMode : null,
                    Model = settings.SelectedModel != "default" ? settings.SelectedModel : null,
                    // Round 3: apply persisted effort level
                    Effort = (string.IsNullOrEmpty(settings.EffortLevel) || settings.EffortLevel == "auto") ? null : settings.EffortLevel
                };

                // B1: Auto-resume last session for this instance (if any)
                if (settings.LastSessionIdPerInstance != null &&
                    settings.LastSessionIdPerInstance.TryGetValue(InstanceId, out var lastSid) &&
                    !string.IsNullOrEmpty(lastSid))
                {
                    config = config.WithResume(lastSid);
                    DebugLog($"Auto-resuming session {lastSid} for instance {InstanceId}");

                    // Fix #2: load historical messages from the session JSONL so the UI
                    // shows previous conversation (CLI --resume only loads CLI context, not UI state).
                    try
                    {
                        var history = Session.SessionJsonlLoader.Load(lastSid!, workingDir);
                        DebugLog($"Loaded {history.Count} historical messages for resume");
                        if (history.Count > 0 && _model != null)
                            _model.LoadHistory(history);
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Load history failed: {ex.Message}");
                    }

                    // Track the resumed session in our store NOW (don't wait for SystemInit
                    // which the CLI may not emit until first user message).
                    try
                    {
                        var tracked = _service?.SessionManager.TrackSession(lastSid!, workingDir);
                        DebugLog($"Tracked resumed session {lastSid} in SessionStore");

                        // Also populate the model's SessionInfo + send session_initialized to the
                        // webview so _currentSessionId is set (required for rename and other actions).
                        if (_model != null)
                        {
                            _model.SessionInfo ??= new Model.SessionInfo(lastSid!);
                            _model.SessionInfo.SessionId = lastSid;
                            _model.SessionInfo.WorkingDirectory = workingDir;
                            if (tracked != null) _model.SessionInfo.Summary = tracked.Summary;
                        }
                        _bridge?.SendToWebview("session_initialized", JsonConvert.SerializeObject(new
                        {
                            sessionId = lastSid,
                            workingDirectory = workingDir,
                            summary = tracked?.Summary
                        }));

                        // Sync tool-window caption on startup (synthetic path doesn't fire OnSessionInitialized).
                        if (!string.IsNullOrEmpty(tracked?.Summary))
                            try { CaptionUpdater?.Invoke(tracked!.Summary!); } catch { }
                    }
                    catch (Exception ex) { DebugLog($"TrackSession failed: {ex.Message}"); }
                }

                DebugLog($"Starting CLI with model={config.Model} effort={config.Effort ?? "auto"}");
                _cliManager?.Start(config);
                DebugLog("CLI Start() completed OK");
            }
            catch (Exception ex)
            {
                DebugLog($"StartCli ERROR: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                _bridge?.SendToWebview("error", JsonConvert.SerializeObject(new { message = ex.Message }));
            }
        }

        private string GetWorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution?.FullName != null)
                    return Path.GetDirectoryName(dte.Solution.FullName) ?? Directory.GetCurrentDirectory();
            }
            catch { }
            return Directory.GetCurrentDirectory();
        }

        #endregion

        #region Public API for commands

        /// <summary>
        /// Called by editor commands to send a message to Claude.
        /// </summary>
        public void SendMessageFromCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _model?.AddUserMessage(message);
            _cliManager?.SendMessage(message);
        }

        /// <summary>
        /// Execute a slash command (e.g. "/new").
        /// </summary>
        public void ExecuteSlashCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            var cmdName = command.Contains(" ") ? command.Substring(0, command.IndexOf(' ')) : command;
            var cmdArgs = command.Contains(" ") ? command.Substring(command.IndexOf(' ') + 1) : null;
            HandleSlashCommand(JsonConvert.SerializeObject(new { command = cmdName, args = cmdArgs }));
        }

        /// <summary>
        /// Insert text into the webview input field (for @-mentions).
        /// </summary>
        public void InsertTextInInput(string text)
        {
            if (_bridge == null || string.IsNullOrEmpty(text)) return;
            _bridge.SendToWebview("insert_at_cursor_input", JsonConvert.SerializeObject(new { text }));
        }

        /// <summary>
        /// Focus the message input in the webview.
        /// </summary>
        public void FocusInput()
        {
            _bridge?.SendToWebview("focus_input", "{}");
        }

        #endregion

        #region IConversationListener

        void IConversationListener.OnSessionInitialized(SessionInfo info)
        {
            // B1: Persist session ID per-instance for auto-resume on next VS launch
            if (!string.IsNullOrEmpty(info.SessionId))
            {
                try
                {
                    var s = Settings.ClaudeSettings.Instance;
                    s.LastSessionIdPerInstance ??= new System.Collections.Generic.Dictionary<int, string>();
                    s.LastSessionIdPerInstance[InstanceId] = info.SessionId!;
                    s.Save();
                }
                catch { }

                // Fix #4: Adopt this sessionId as the tracked current session so that
                // rename + SaveCurrentSession actually persist for both fresh sessions
                // (StartNewSession was never called on auto-resume) and --resume'd ones.
                try
                {
                    _service?.SessionManager.TrackSession(info.SessionId!, GetWorkingDirectory());
                }
                catch { }
            }

            // Round 3: include summary so the editable header title displays the session name
            // (falls back to persisted summary from SessionManager if model didn't have one yet)
            string? summary = info.Summary;
            if (string.IsNullOrEmpty(summary) && !string.IsNullOrEmpty(info.SessionId))
            {
                try
                {
                    var stored = _service?.SessionManager.ListSessions()
                        .FirstOrDefault(s => s.SessionId == info.SessionId);
                    if (stored != null) summary = stored.Summary;
                }
                catch { }
            }

            Dispatch(() => _bridge?.SendToWebview("session_initialized", JsonConvert.SerializeObject(new
            {
                sessionId = info.SessionId,
                model = info.Model,
                cwd = info.WorkingDirectory,
                permissionMode = info.PermissionMode,
                summary = summary
            })));

            // Sync tool-window caption with the session summary on init (restore across VS restarts).
            if (!string.IsNullOrEmpty(summary))
                try { CaptionUpdater?.Invoke(summary!); } catch { }
        }

        void IConversationListener.OnUserMessageAdded(MessageBlock block)
        {
            Dispatch(() => _bridge?.SendToWebview("user_message_added", BuildMessageBlockJson(block)));
        }

        void IConversationListener.OnAssistantMessageStarted(MessageBlock block)
        {
            Dispatch(() => _bridge?.SendToWebview("assistant_message_started", BuildMessageBlockJson(block)));
        }

        void IConversationListener.OnStreamingTextAppended(MessageBlock block, string delta)
        {
            Dispatch(() => _bridge?.SendToWebview("streaming_text_appended", JsonConvert.SerializeObject(new { delta })));
        }

        void IConversationListener.OnToolCallStarted(MessageBlock block, MessageBlock.ToolCallSegment toolCall)
        {
            Dispatch(() => _bridge?.SendToWebview("tool_call_started", BuildToolCallJson(toolCall)));
        }

        void IConversationListener.OnToolCallInputDelta(MessageBlock block, MessageBlock.ToolCallSegment toolCall, string delta)
        {
            Dispatch(() => _bridge?.SendToWebview("tool_call_input_delta", JsonConvert.SerializeObject(new
            {
                toolId = toolCall.ToolId,
                delta,
                summary = toolCall.GetSummary()
            })));
        }

        void IConversationListener.OnToolCallInputComplete(MessageBlock block, MessageBlock.ToolCallSegment toolCall)
        {
            // Auto-save dirty editors before tool execution (if enabled)
            if (Settings.ClaudeSettings.Instance.AutoSaveBeforeTools)
            {
                Dispatch(() =>
                {
                    try
                    {
                        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                        dte?.Documents?.SaveAll();
                    }
                    catch { }
                });
            }

            // Snapshot file before tool executes (for revert)
            SnapshotForCheckpoint(toolCall);

            Dispatch(() => _bridge?.SendToWebview("tool_call_input_complete", JsonConvert.SerializeObject(new
            {
                toolId = toolCall.ToolId,
                summary = toolCall.GetSummary()
            })));
        }

        void IConversationListener.OnToolCallCompleted(MessageBlock block, MessageBlock.ToolCallSegment toolCall)
        {
            Dispatch(() => _bridge?.SendToWebview("tool_call_completed", BuildToolCallJson(toolCall)));

            // For Edit/Write tools that completed successfully, register the edit and stage it for review.
            if ((toolCall.ToolName == "Edit" || toolCall.ToolName == "Write")
                && toolCall.Status == MessageBlock.ToolStatus.Completed
                && _service != null)
            {
                try
                {
                    var input = toolCall.Input;
                    if (string.IsNullOrEmpty(input)) return;
                    var json = JObject.Parse(input!);
                    var filePath = json.Value<string>("file_path");
                    if (string.IsNullOrEmpty(filePath)) return;

                    var edit = _service.EditDecisionManager.RecordCompletedEdit(filePath!);
                    if (edit != null)
                    {
                        var fileName = System.IO.Path.GetFileName(edit.FilePath);
                        Dispatch(() => _bridge?.SendToWebview("edit_staged", JsonConvert.SerializeObject(new
                        {
                            editId = edit.Id.ToString(),
                            fileName = fileName,
                            filePath = edit.FilePath
                        })));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OnToolCallCompleted edit_staged] {ex.Message}");
                }
            }
        }

        void IConversationListener.OnAssistantMessageCompleted(MessageBlock block)
        {
            Dispatch(() => _bridge?.SendToWebview("assistant_message_completed", BuildMessageBlockJson(block)));
        }

        void IConversationListener.OnResultReceived(UsageInfo usage)
        {
            // Auto-save session after each turn
            try { _service?.SessionManager.SaveCurrentSession(_model!); } catch { }

            Dispatch(() => _bridge?.SendToWebview("result_received", JsonConvert.SerializeObject(new
            {
                inputTokens = usage.TotalInputTokens,
                outputTokens = usage.TotalOutputTokens,
                totalTokens = usage.TotalTokens,
                costUsd = usage.TotalCostUsd,
                durationMs = usage.TotalDurationMs,
                turns = usage.TotalTurns,
                formattedCost = usage.FormatCost(),
                formattedTokens = usage.FormatTokens(),
                formattedDuration = usage.FormatDuration()
            })));
        }

        void IConversationListener.OnPermissionRequested(string? toolUseId, string toolName, string description, string? requestId, object? toolInput)
        {
            // Store tool input for echoing back in control_response
            if (requestId != null && toolInput != null)
                _pendingToolInputs[requestId] = toolInput;

            Dispatch(() => _bridge?.SendToWebview("permission_requested", JsonConvert.SerializeObject(new
            {
                toolUseId,
                toolName,
                description,
                requestId
            })));
        }

        void IConversationListener.OnExtendedThinkingStarted()
        {
            Dispatch(() => _bridge?.SendToWebview("extended_thinking_started", "{}"));
        }

        void IConversationListener.OnExtendedThinkingEnded()
        {
            Dispatch(() => _bridge?.SendToWebview("extended_thinking_ended", "{}"));
        }

        void IConversationListener.OnError(string error)
        {
            Dispatch(() => _bridge?.SendToWebview("error", JsonConvert.SerializeObject(new { message = error })));
        }

        void IConversationListener.OnConversationCleared()
        {
            Dispatch(() => _bridge?.SendToWebview("conversation_cleared", "{}"));
        }

        void IConversationListener.OnRateLimit(string? message, long? resetAtEpochSec)
        {
            Dispatch(() => _bridge?.SendToWebview("rate_limit",
                JsonConvert.SerializeObject(new { message = message ?? "Rate limit hit", resetAt = resetAtEpochSec })));
        }

        void IConversationListener.OnSilentEmptyShouldRetry(string lastUserPrompt)
        {
            // Eclipse fix #6: silently re-send the last prompt once. Done on UI thread so we
            // can also flip the Send/Stop button back to Stop visually.
            Dispatch(() =>
            {
                if (_cliManager != null && _cliManager.IsRunning && !string.IsNullOrEmpty(lastUserPrompt))
                {
                    try { _cliManager.SendMessage(lastUserPrompt); } catch { }
                    _bridge?.SendToWebview("system_message",
                        JsonConvert.SerializeObject(new { text = "↻ auto-retry (hook returned empty)" }));
                }
            });
        }

        #endregion

        #region ICliStateListener

        void ICliStateListener.OnStateChanged(ClaudeCliManager.ProcessState oldState, ClaudeCliManager.ProcessState newState)
        {
            Dispatch(() =>
            {
                // Map ProcessState to the string values JS expects
                var stateStr = newState switch
                {
                    ClaudeCliManager.ProcessState.Running => "connected",
                    ClaudeCliManager.ProcessState.Error => "error",
                    _ => "disconnected"
                };
                _bridge?.SendToWebview("cli_state_changed", JsonConvert.SerializeObject(new
                {
                    state = stateStr
                }));
            });
        }

        #endregion

        #region VS IDE Integration

        /// <summary>
        /// Fast synchronous theme check for constructor (before DTE is available).
        /// Uses Windows system colors as a proxy.
        /// </summary>
        private static bool IsVsLightTheme()
        {
            try
            {
                var bg = SystemColors.WindowBrush.Color;
                var brightness = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B);
                return brightness >= 128;
            }
            catch
            {
                return false; // default to dark
            }
        }

        private void DetectAndApplyTheme()
        {
            var theme = DetectVsTheme();
            _bridge?.SendToWebview("set_theme", JsonConvert.SerializeObject(new { theme }));
        }

        private string DetectVsTheme()
        {
            try
            {
                // Use the VS shell to get the current theme's background color
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                if (dte != null)
                {
                    var props = dte.Properties["Environment", "General"];
                    // The "CurrentTheme" property gives the theme GUID
                    // Dark: {1ded0138-47ce-435e-84ef-9ec1f439b749}
                    // Light: {de3dbbcd-f642-433c-8353-8f1df4370aba}
                    // Blue: {a4d6a176-b948-4b29-8c66-53c97a1ed7d0}
                    try
                    {
                        var themeGuid = props.Item("CurrentTheme")?.Value?.ToString()?.ToLower() ?? "";
                        if (themeGuid.Contains("1ded0138")) return "dark";   // Dark theme
                        if (themeGuid.Contains("de3dbbcd")) return "light";  // Light theme
                        if (themeGuid.Contains("a4d6a176")) return "light";  // Blue theme (light-ish)
                    }
                    catch { }
                }

                // Fallback: check system colors
                var bg = SystemColors.WindowBrush.Color;
                var brightness = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B);
                return brightness < 128 ? "dark" : "light";
            }
            catch
            {
                return "dark"; // safe default
            }
        }

        #endregion

        #region JSON Serialization Helpers (matching IntelliJ format)

        private static string BuildMessageBlockJson(MessageBlock block)
        {
            var segments = new JArray();
            foreach (var seg in block.Segments)
            {
                if (seg is MessageBlock.TextSegment textSeg)
                {
                    segments.Add(new JObject
                    {
                        ["type"] = "text",
                        ["text"] = textSeg.Text
                    });
                }
                else if (seg is MessageBlock.ToolCallSegment toolSeg)
                {
                    segments.Add(JObject.Parse(BuildToolCallJson(toolSeg)));
                }
            }
            var obj = new JObject
            {
                ["role"] = block.MessageRole.ToString().ToLower(),
                ["timestamp"] = block.Timestamp,
                ["segments"] = segments
            };
            return obj.ToString(Formatting.None);
        }

        private static string BuildToolCallJson(MessageBlock.ToolCallSegment toolCall)
        {
            var obj = new JObject
            {
                ["type"] = "tool_use",
                ["toolId"] = toolCall.ToolId,
                ["toolName"] = toolCall.ToolName,
                ["displayName"] = toolCall.GetDisplayName(),
                ["summary"] = toolCall.GetSummary(),
                ["input"] = toolCall.Input,
                ["output"] = toolCall.Output,
                ["status"] = toolCall.Status.ToString().ToLower()
            };
            return obj.ToString(Formatting.None);
        }

        #endregion

        /// <summary>
        /// Snapshot a file before a tool modifies it (for checkpoint/revert).
        /// Extracts file_path from the tool's input JSON.
        /// </summary>
        private void SnapshotForCheckpoint(MessageBlock.ToolCallSegment toolCall)
        {
            if (_service == null) return;
            var toolName = toolCall.ToolName;
            if (toolName != "Edit" && toolName != "Write") return;

            try
            {
                var input = toolCall.Input;
                if (input == null) return;
                // Extract file_path from JSON input
                var json = JObject.Parse(input);
                var filePath = json.Value<string>("file_path");
                if (!string.IsNullOrEmpty(filePath))
                    _service.CheckpointManager.Snapshot(filePath!);
            }
            catch { }
        }

        private void Dispatch(Action action)
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.BeginInvoke(action);
        }

        private static void DebugLog(string msg)
        {
            try
            {
                // Write log NEXT TO THE DLL (VS can definitely access this directory)
                var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                var logPath = Path.Combine(assemblyDir, "claude_debug.log");
                File.AppendAllText(logPath, $"[Panel {DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }
    }
}
