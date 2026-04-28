using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ClaudeCode.Cli
{
    /// <summary>
    /// Manages the Claude CLI process lifecycle.
    /// Port of com.anthropic.claude.intellij.cli.ClaudeCliManager.
    /// </summary>
    public class ClaudeCliManager : IDisposable
    {
        private const string CLAUDE_CODE_ENTRYPOINT = "vs2022-plugin";

        public enum ProcessState
        {
            NotStarted, Starting, Running, Stopping, Stopped, Error
        }

        private Process? _process;
        private StreamWriter? _processWriter;
        private Thread? _readerThread;
        private Thread? _errorThread;
        private Timer? _healthChecker;
        private readonly NdjsonProtocolHandler _protocolHandler = new();

        private readonly List<ICliMessageListener> _messageListeners = new();
        private readonly List<ICliStateListener> _stateListeners = new();
        private readonly object _lock = new();

        private volatile ProcessState _state = ProcessState.NotStarted;
        private volatile bool _busy;
        private volatile int _lastExitCode = -1;
        private CliProcessConfig? _storedConfig;

        public string? SessionId { get; private set; }
        public string? CurrentModel { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public ProcessState State => _state;
        public bool IsRunning => _state == ProcessState.Running;
        /// <summary>Eclipse fix #10: includes Starting so callers don't try to spawn duplicate CLIs.</summary>
        public bool IsRunningOrStarting => _state == ProcessState.Running || _state == ProcessState.Starting;
        public bool IsBusy => _busy;
        public int LastExitCode => _lastExitCode;

        public void AddMessageListener(ICliMessageListener listener)
        {
            lock (_lock) { if (!_messageListeners.Contains(listener)) _messageListeners.Add(listener); }
        }
        public void RemoveMessageListener(ICliMessageListener listener) { lock (_lock) _messageListeners.Remove(listener); }
        public void AddStateListener(ICliStateListener listener)
        {
            lock (_lock) { if (!_stateListeners.Contains(listener)) _stateListeners.Add(listener); }
        }
        public void RemoveStateListener(ICliStateListener listener) { lock (_lock) _stateListeners.Remove(listener); }

        /// <summary>Starts the Claude CLI process with the given configuration.</summary>
        public void Start(CliProcessConfig config)
        {
            lock (_lock)
            {
                if (_state == ProcessState.Running || _state == ProcessState.Starting)
                    return;

                var cliPath = !string.IsNullOrEmpty(config.CliPath) ? config.CliPath : GetCliPath();
                if (string.IsNullOrEmpty(cliPath))
                    throw new IOException("Claude CLI path is not configured.");

                FireStateChanged(ProcessState.Starting);
                WorkingDirectory = config.WorkingDirectory;
                _storedConfig = config;

                var args = BuildArguments(config);

                // On Windows, .cmd wrappers don't pass stdin properly.
                // Resolve to the actual node.exe + cli.js entry point.
                var fileName = cliPath!;
                var arguments = args;
                fileName = ResolveCliPath(fileName, ref arguments);

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = config.WorkingDirectory ?? "",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };

                psi.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"] = CLAUDE_CODE_ENTRYPOINT;
                psi.EnvironmentVariables["FORCE_COLOR"] = "0";
                psi.EnvironmentVariables.Remove("NODE_OPTIONS");
                psi.EnvironmentVariables.Remove("CLAUDECODE");
                psi.EnvironmentVariables.Remove("CLAUDE_CODE_OAUTH_TOKEN");

                var apiKey = Settings.ClaudeSettings.Instance.ApiKey;
                if (!string.IsNullOrEmpty(apiKey))
                    psi.EnvironmentVariables["ANTHROPIC_API_KEY"] = apiKey;

                try
                {
                    _process = Process.Start(psi)
                        ?? throw new IOException("Failed to start Claude CLI process.");
                    // Force UTF-8 stdin so non-ASCII (Hebrew, etc.) is not mangled by system codepage
                    _processWriter = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };

                    FireStateChanged(ProcessState.Running);

                    _readerThread = new Thread(ReadProcessOutput) { IsBackground = true, Name = "claude-cli-reader" };
                    _readerThread.Start();

                    _errorThread = new Thread(ReadProcessErrors) { IsBackground = true, Name = "claude-cli-error-reader" };
                    _errorThread.Start();

                    StartHealthMonitor();
                }
                catch (Exception)
                {
                    FireStateChanged(ProcessState.Error);
                    throw;
                }
            }
        }

        /// <summary>Stops the CLI process.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (_state == ProcessState.Stopped || _state == ProcessState.NotStarted)
                    return;

                FireStateChanged(ProcessState.Stopping);
                _busy = false;

                _healthChecker?.Dispose();
                _healthChecker = null;

                try { _processWriter?.Close(); } catch { }

                if (_process != null && !_process.HasExited)
                {
                    try
                    {
                        // Kill entire process tree (taskkill /T /F) so node.exe children
                        // and any MCP helpers spawned by claude CLI are also terminated.
                        var pid = _process.Id;
                        try
                        {
                            using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskkill", $"/T /F /PID {pid}")
                            {
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                            });
                            killer?.WaitForExit(2000);
                        }
                        catch { }
                        if (!_process.HasExited) _process.Kill();
                        _process.WaitForExit(3000);
                    }
                    catch { }
                }

                _process?.Dispose();
                _process = null;

                FireStateChanged(ProcessState.Stopped);
            }
        }

        public void SendMessage(string userContent)
        {
            SendRawJson(CliMessage.CreateUserInputJson(userContent));
            _busy = true;
        }

        public void SendRichMessage(string textContent, List<byte[]>? imageDataList)
        {
            SendRawJson(CliMessage.CreateUserInputJsonRich(textContent, imageDataList));
            _busy = true;
        }

        public void SendPermissionResponse(string toolUseId, bool allow)
        {
            SendRawJson(CliMessage.CreatePermissionResponse(toolUseId, allow));
        }

        public void SendControlResponse(string requestId, bool allow, object? toolInput = null)
        {
            SendRawJson(CliMessage.CreateControlResponse(requestId, allow, toolInput));
        }

        public void SendRawJson(string json)
        {
            lock (_lock)
            {
                if (_state != ProcessState.Running || _processWriter == null)
                    return;

                try
                {
                    _processWriter.WriteLine(json);
                    _processWriter.Flush();
                }
                catch (Exception ex)
                {
                    foreach (var listener in GetMessageListeners())
                    {
                        try { listener.OnConnectionError(ex); } catch { }
                    }
                }
            }
        }

        public void Restart()
        {
            if (_storedConfig == null)
                throw new InvalidOperationException("No previous configuration stored.");
            Stop();
            Start(_storedConfig);
        }

        public void Restart(CliProcessConfig newConfig)
        {
            Stop();
            Start(newConfig);
        }

        /// <summary>
        /// Interrupts the current query (e.g. on Stop button) and restarts the CLI with
        /// <c>--resume &lt;sessionId&gt;</c> so conversation history is preserved.
        /// Port from Eclipse Phase 5. If sessionId is null/empty, falls back to plain Stop().
        /// </summary>
        public void InterruptCurrentQuery(string? resumeSessionId)
        {
            if (_storedConfig == null)
            {
                Stop();
                return;
            }

            if (string.IsNullOrEmpty(resumeSessionId))
            {
                Stop();
                return;
            }

            var resumedConfig = _storedConfig.WithResume(resumeSessionId!);
            Stop();
            Start(resumedConfig);
        }

        /// <summary>Returns the CLI path from settings, or attempts to find it automatically.</summary>
        public static string? GetCliPath()
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine($"[GetCliPath] called at {DateTime.Now:HH:mm:ss}");

            // STRATEGY 1: Use CLI bundled in extension directory (most reliable - avoids File.Exists sandbox issues)
            try
            {
                var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                log.AppendLine($"  assemblyDir: {assemblyDir}");

                // Check for bundled cli.js (relative path in claude_cli_path.txt)
                var pathFile = Path.Combine(assemblyDir, "claude_cli_path.txt");
                log.AppendLine($"  PathFile: {pathFile} exists={File.Exists(pathFile)}");
                if (File.Exists(pathFile))
                {
                    var rawPath = File.ReadAllText(pathFile).Trim();
                    // Resolve relative path from assembly directory
                    var cliJsPath = Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(assemblyDir, rawPath);
                    log.AppendLine($"  Resolved CLI path: '{cliJsPath}' exists={File.Exists(cliJsPath)}");
                    if (File.Exists(cliJsPath))
                    {
                        WriteDebugLog(log.ToString());
                        return cliJsPath;
                    }
                }

                // Check for bundled node.exe + claude-cli/cli.js directly
                var bundledCliJs = Path.Combine(assemblyDir, "claude-cli", "cli.js");
                log.AppendLine($"  Bundled cli.js: {bundledCliJs} exists={File.Exists(bundledCliJs)}");
                if (File.Exists(bundledCliJs))
                {
                    WriteDebugLog(log.ToString());
                    return bundledCliJs;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"  Bundled CLI error: {ex.Message}");
            }

            // STRATEGY 2: Hardcoded known location
            var hardcodedCmd = @"C:\Users\Administrator\AppData\Roaming\npm\claude.cmd";
            try
            {
                log.AppendLine($"  Hardcoded: {hardcodedCmd} exists={File.Exists(hardcodedCmd)}");
                if (File.Exists(hardcodedCmd))
                {
                    WriteDebugLog(log.ToString());
                    return hardcodedCmd;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"  Hardcoded error: {ex.Message}");
            }

            try
            {
                // 1. Check user-configured path
                var configuredPath = Settings.ClaudeSettings.Instance.CliPath;
                log.AppendLine($"  CliPath='{configuredPath}' exists={(!string.IsNullOrEmpty(configuredPath) ? File.Exists(configuredPath).ToString() : "empty")}");
                if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
                {
                    WriteDebugLog(log.ToString());
                    return configuredPath;
                }

                // 2. Check last-known-good path
                var lastKnown = Settings.ClaudeSettings.Instance.LastKnownCliPath;
                log.AppendLine($"  LastKnownCliPath='{lastKnown}' exists={(!string.IsNullOrEmpty(lastKnown) ? File.Exists(lastKnown).ToString() : "empty")}");
                if (!string.IsNullOrEmpty(lastKnown) && File.Exists(lastKnown))
                {
                    WriteDebugLog(log.ToString());
                    return lastKnown;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"  Settings error: {ex.Message}");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            log.AppendLine($"  home='{home}'");

            // NOTE: Do NOT search for native claude.exe in Roaming\Claude\claude-code\*\
            // That's the Claude Desktop app (Electron), not the Claude Code CLI.

            var commonPaths = new[]
            {
                Path.Combine(home, "AppData", "Roaming", "npm", "claude.cmd"),
                Path.Combine(home, "AppData", "Local", "Programs", "claude", "claude.exe"),
                @"C:\Program Files\claude\claude.exe",
                @"C:\Program Files\nodejs\claude.cmd",
                Path.Combine(home, ".local", "bin", "claude"),
                Path.Combine(home, ".npm", "bin", "claude"),
                "/usr/local/bin/claude",
                "/usr/bin/claude",
                "/opt/homebrew/bin/claude"
            };

            foreach (var path in commonPaths)
            {
                var exists = File.Exists(path);
                log.AppendLine($"  try '{path}' exists={exists}");
                if (exists)
                {
                    SaveLastKnownPath(path);
                    WriteDebugLog(log.ToString());
                    return path;
                }
            }

            // Try 'where' on Windows
            try
            {
                var psi = new ProcessStartInfo("where", "claude")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadLine();
                    p.WaitForExit(3000);
                    log.AppendLine($"  where output='{output}'");
                    if (!string.IsNullOrEmpty(output) && File.Exists(output.Trim()))
                    {
                        SaveLastKnownPath(output.Trim());
                        WriteDebugLog(log.ToString());
                        return output.Trim();
                    }
                }
            }
            catch (Exception ex) { log.AppendLine($"  where error: {ex.Message}"); }

            log.AppendLine("  RESULT: null (not found)");
            WriteDebugLog(log.ToString());
            return null;
        }

        private static void WriteDebugLog(string content)
        {
            try
            {
                // Write next to our DLL (same as Panel's DebugLog - we KNOW this works)
                var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                var logPath = Path.Combine(assemblyDir, "claude_debug.log");
                File.AppendAllText(logPath, content + "\n---\n");
            }
            catch { }
        }

        /// <summary>Find native claude.exe in %APPDATA%\Claude\claude-code\{version}\</summary>
        private static string? FindNativeClaudeExe(string home)
        {
            try
            {
                var claudeCodeDir = Path.Combine(home, "AppData", "Roaming", "Claude", "claude-code");
                if (!Directory.Exists(claudeCodeDir)) return null;

                // Find latest version directory containing claude.exe
                var dirs = Directory.GetDirectories(claudeCodeDir);
                // Sort descending to get latest version first
                Array.Sort(dirs);
                Array.Reverse(dirs);
                foreach (var dir in dirs)
                {
                    var exe = Path.Combine(dir, "claude.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
            return null;
        }

        private static void SaveLastKnownPath(string path)
        {
            try
            {
                Settings.ClaudeSettings.Instance.LastKnownCliPath = path;
                Settings.ClaudeSettings.Instance.Save();
            }
            catch { }
        }

        /// <summary>
        /// Resolves a .cmd wrapper to the underlying node.exe + cli.js command.
        /// This is necessary because Process.Start with UseShellExecute=false
        /// doesn't properly handle stdin redirection through .cmd files.
        /// </summary>
        private static string ResolveCliPath(string cliPath, ref string arguments)
        {
            // If it's already a .js file, resolve to node.exe + cli.js
            if (cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                // First check for node.exe bundled in extension directory
                var cliDir = Path.GetDirectoryName(cliPath) ?? "";
                var extensionDir = Path.GetDirectoryName(cliDir) ?? cliDir; // go up from claude-cli/
                var bundledNode = Path.Combine(extensionDir, "node.exe");
                if (File.Exists(bundledNode))
                {
                    arguments = $"\"{cliPath}\" {arguments}";
                    return bundledNode;
                }
                // Fallback to Program Files
                var nodeExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "nodejs", "node.exe");
                if (File.Exists(nodeExe))
                {
                    arguments = $"\"{cliPath}\" {arguments}";
                    return nodeExe;
                }
            }

            if (!cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                return cliPath;

            // claude.cmd lives in npm prefix dir. The actual entry point is:
            // node.exe <npm-prefix>/node_modules/@anthropic-ai/claude-code/cli.js
            var npmDir = Path.GetDirectoryName(cliPath);
            if (npmDir != null)
            {
                var cliJs = Path.Combine(npmDir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
                if (File.Exists(cliJs))
                {
                    // Find node.exe - check same dir first, then PATH
                    var nodeExe = Path.Combine(npmDir, "node.exe");
                    if (!File.Exists(nodeExe))
                    {
                        // Check Program Files
                        nodeExe = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            "nodejs", "node.exe");
                    }
                    if (File.Exists(nodeExe))
                    {
                        arguments = $"\"{cliJs}\" {arguments}";
                        System.Diagnostics.Debug.WriteLine($"[CLI] Resolved: {nodeExe} {arguments}");
                        return nodeExe;
                    }
                }
            }

            // Fallback: run through cmd.exe
            arguments = $"/c \"{cliPath}\" {arguments}";
            return "cmd.exe";
        }

        public static string? GetVersion()
        {
            var cliPath = GetCliPath();
            if (cliPath == null) return null;
            try
            {
                var psi = new ProcessStartInfo(cliPath, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadLine();
                    p.WaitForExit(3000);
                    return output?.Trim();
                }
            }
            catch { }
            return null;
        }

        private string BuildArguments(CliProcessConfig config)
        {
            var args = new List<string>
            {
                "--output-format", "stream-json",
                "--verbose",
                "--input-format", "stream-json",
                "--permission-prompt-tool", "stdio",
                "--include-partial-messages"
            };

            if (!string.IsNullOrEmpty(config.PermissionMode))
                args.AddRange(new[] { "--permission-mode", config.PermissionMode! });

            // Effort/thinking budget level (port from Eclipse). Skip if "auto" or empty (use CLI default).
            if (!string.IsNullOrEmpty(config.Effort) && config.Effort != "auto")
                args.AddRange(new[] { "--effort", config.Effort! });

            if (!string.IsNullOrEmpty(config.Model))
                args.AddRange(new[] { "--model", config.Model! });

            if (!string.IsNullOrEmpty(config.SessionId))
                args.AddRange(new[] { "--session-id", config.SessionId! });

            if (config.ContinueSession)
                args.Add("--continue");

            if (!string.IsNullOrEmpty(config.ResumeSessionId))
                args.AddRange(new[] { "--resume", config.ResumeSessionId! });

            if (config.MaxTurns > 0)
                args.AddRange(new[] { "--max-turns", config.MaxTurns.ToString() });

            if (!string.IsNullOrEmpty(config.AppendSystemPrompt))
                args.AddRange(new[] { "--append-system-prompt", config.AppendSystemPrompt! });

            if (config.AllowedTools != null)
            {
                foreach (var tool in config.AllowedTools)
                    args.AddRange(new[] { "--allowedTools", tool });
            }

            if (config.AdditionalDirs != null)
            {
                foreach (var dir in config.AdditionalDirs)
                    args.AddRange(new[] { "--add-dir", dir });
            }

            // Apply settings-based options
            var settings = Settings.ClaudeSettings.Instance;
            if (settings.MaxTokens > 0)
                args.AddRange(new[] { "--max-tokens", settings.MaxTokens.ToString() });
            if (!string.IsNullOrEmpty(settings.SystemPrompt) && string.IsNullOrEmpty(config.AppendSystemPrompt))
                args.AddRange(new[] { "--append-system-prompt", settings.SystemPrompt! });

            return string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }

        private void ReadProcessOutput()
        {
            try
            {
                using var reader = _process?.StandardOutput;
                if (reader == null) return;

                string? line;
                while (_state == ProcessState.Running && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Diagnostics.Log("DIAG-MSG", line.Length > 500 ? line.Substring(0, 500) + "..." : line);
                    try
                    {
                        ProcessNdjsonLine(line);
                    }
                    catch (Exception ex)
                    {
                        foreach (var listener in GetMessageListeners())
                        {
                            try { listener.OnParseError(line, ex); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_state == ProcessState.Running)
                {
                    foreach (var listener in GetMessageListeners())
                    {
                        try { listener.OnConnectionError(ex); } catch { }
                    }
                }
            }

            if (_state == ProcessState.Running)
                FireStateChanged(ProcessState.Stopped);
        }

        private void ReadProcessErrors()
        {
            try
            {
                using var reader = _process?.StandardError;
                if (reader == null) return;

                string? line;
                while (_state == ProcessState.Running && (line = reader.ReadLine()) != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[CLI stderr] {line}");
                    Diagnostics.Log("DIAG-STDERR", line);
                }
            }
            catch { }
        }

        private void ProcessNdjsonLine(string line)
        {
            var message = _protocolHandler.ParseLine(line);
            if (message == null || message == CliMessage.Ignored)
                return;

            if (message is CliMessage.SystemInit init)
            {
                SessionId = init.SessionId;
                CurrentModel = init.Model;
            }

            if (message is CliMessage.ResultMessage)
                _busy = false;

            foreach (var listener in GetMessageListeners())
            {
                try { listener.OnMessage(message); } catch { }
            }
        }

        private void StartHealthMonitor()
        {
            _healthChecker?.Dispose();
            _healthChecker = new Timer(_ =>
            {
                if (_process != null && _process.HasExited && _state == ProcessState.Running)
                {
                    _lastExitCode = _process.ExitCode;
                    FireStateChanged(ProcessState.Error);
                    _healthChecker?.Dispose();
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        private void FireStateChanged(ProcessState newState)
        {
            var oldState = _state;
            if (oldState == newState) return;
            _state = newState;
            Diagnostics.Log("DIAG-STATE", $"{oldState} -> {newState}");

            List<ICliStateListener> listeners;
            lock (_lock) { listeners = new List<ICliStateListener>(_stateListeners); }
            foreach (var listener in listeners)
            {
                try { listener.OnStateChanged(oldState, newState); } catch { }
            }
        }

        private List<ICliMessageListener> GetMessageListeners()
        {
            lock (_lock) { return new List<ICliMessageListener>(_messageListeners); }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
