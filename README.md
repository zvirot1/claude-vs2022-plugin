# Claude Code for Visual Studio 2022

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![VS 2022](https://img.shields.io/badge/Visual%20Studio-2022-purple)](https://visualstudio.microsoft.com/)
[![.NET Framework](https://img.shields.io/badge/.NET-4.7.2-blue)](https://dotnet.microsoft.com/)

A Visual Studio 2022 extension that brings the [Claude Code](https://docs.anthropic.com/claude-code) CLI into the IDE as a fully-featured chat panel.
Feature parity with the official Anthropic plugins for **IntelliJ**, **Eclipse**, and **VS Code** — same conversation model, same session protocol, same UX patterns.

---

## ✨ Features

### Chat Experience
- **Full-featured chat panel** (View → Other Windows → Claude Code) backed by WebView2
- **Streaming markdown rendering** with syntax-highlighted code blocks (Apply / Insert / Copy buttons)
- **Hebrew/Arabic RTL** input + display, UTF-8 stdin so non-ASCII never gets garbled
- **Welcome screen** with quick-tip tiles (`@` mention, `/` slash, ⇧Tab cycle, ⚡ effort)
- **Rate-limit banner** with countdown when the API throttles you

### Session Management
- **Multi-instance** — open several independent Claude windows via the 📋 button, each with its own CLI process and conversation context
- **Tab persistence** — windows you had open are restored automatically next launch
- **Auto-resume** — last session reopens with full message history loaded from JSONL
- **Session History dialog** (📚) — 4-column table (date, summary, model, msg count) with preview pane, rename, delete, resume
- **Rename in panel** — click the title at the top of the panel to rename in place; persists to the session store

### Context Pins (Amazon Q parity)
- **Active File chip** inside the input frame — shows the file currently open in the editor (📄 + filename + "current file" hint, blue accent)
- Auto-updates when you switch tabs in the IDE
- Click **×** to dismiss for the current message; re-appears on tab switch
- Sends as `<file path="rel">contents</file>` context to the CLI (hidden from the user's chat bubble)
- **@-mention** and **attach** chips coexist alongside the active-file pin
- **Inline image previews** — pasted/attached images appear as 200×200 thumbnails in your message bubble; click to open full-size

### CLI Controls
- **Model selector** — click the model badge to switch (sonnet, opus, haiku, or any custom id); persisted custom models
- **Effort level** — ⚡ Auto / Low / Medium / High / Max with hot-swap (`--effort` flag, restart + `--resume`)
- **Permission mode** — Ask / Auto-edits / Plan via popup (Shift+Tab cycles)
- **Stop** kills the entire process tree (`taskkill /T /F`) so node helpers and MCP children don't leak
- **Send** debounces 300ms to ignore double-Enter

### Reliability fixes (ports from Eclipse + IntelliJ)
- Send button switches back to "Send" the moment the assistant turn finishes (not after the slow `result` event)
- Race-safe streaming: late deltas are dropped after the message is finalized (no "HelloHello" duplication)
- System-message subtype split (`init` vs `hook_started/hook_progress/hook_response/compact_boundary`)
- AWS SSO / unauthorized / token-expired hook errors surface to the user with actionable hints
- "Silent empty result" detection (UserPromptSubmit hook block signature) + one auto-retry
- Friendly hook-block error message with workarounds and `claude --debug` instructions
- Settings file reads/writes retry on IO contention from parallel CLI processes
- Per-instance WebView2 user-data folder so multi-window doesn't collide
- Synthetic `session_initialized` on auto-resume so rename and other actions don't depend on the CLI emitting SystemInit

### Editor Integration
- **VSCT commands** with shortcuts: Ctrl+Alt+C open Claude, Ctrl+Alt+S send selection, Alt+K insert @-mention
- Right-click in editor → Send to Claude / Explain / Review / Refactor / Analyze
- **Ctrl+V** image paste in input box

### Diagnostics
- Set env var `CLAUDE_DIAG=1` (or toggle `DiagEnabled` in settings.json) to log every NDJSON line, state change, and stderr line to `%LOCALAPPDATA%\ClaudeCode\diag.log`
- `claude_debug.log` next to the DLL captures panel lifecycle events
- `%LOCALAPPDATA%\ClaudeCode\debug.log` captures all webview ↔ C# bridge messages

---

## 📦 Installation

### Prerequisites
1. **Visual Studio 2022** (Community / Professional / Enterprise, 17.0 or later)
2. **Claude Code CLI** — install via:
   ```bash
   npm install -g @anthropic-ai/claude-code
   ```
3. **Anthropic API key** (or AWS Bedrock / Vertex AI configured) — see [Claude Code authentication](https://docs.anthropic.com/claude-code).

### Install the extension
1. Download the latest `ClaudeCode-1.0.5-<timestamp>.vsix` from [Releases](https://github.com/zvirot1/claude-vs2022-plugin/releases).
2. **Close all Visual Studio instances.**
3. Double-click the `.vsix` file. The VSIX Installer opens.
4. Tick your VS edition → **Install**.
5. Re-open Visual Studio → **View → Other Windows → Claude Code**.

If install fails with "not a valid VSIX package," see the troubleshooting section below.

---

## ⚙️ Configuration

Settings live at `%LOCALAPPDATA%\ClaudeCode\settings.json` (also accessible via **Tools → Options → Claude Code**).

| Key | Default | Description |
|---|---|---|
| `CliPath` | `""` | Optional explicit path to `claude.exe`. If empty, auto-detected from PATH or `claude_cli_path.txt` next to the DLL. |
| `SelectedModel` | `"default"` | `default`, `sonnet`, `opus`, `haiku`, or a full model id like `claude-opus-4-6`. |
| `EffortLevel` | `"auto"` | `auto` (no flag), `low`, `medium`, `high`, `max`. |
| `InitialPermissionMode` | `"default"` | `default`, `acceptEdits`, `bypassPermissions`, `plan`. |
| `UseCtrlEnterToSend` | `false` | Require Ctrl+Enter to send (Enter inserts newline). |
| `RespectGitIgnore` | `true` | Skip `.gitignore`d files in @-mention search. |
| `Autosave` | `true` | Auto-save dirty editor buffers before tools that read files. |
| `AutoSaveBeforeTools` | `false` | Save all dirty buffers before any tool call. |
| `ShowCost` | `true` | Show $ cost in the status footer. |
| `ShowStreaming` | `true` | Show partial messages as they stream. |
| `SessionHistoryLimit` | `100` | Max number of sessions retained in the History dialog. |
| `DiagEnabled` | `false` | Verbose `[DIAG]` logging to `diag.log` (also via env var `CLAUDE_DIAG=1`). |
| `LastSessionIdPerInstance` | `{}` | Per-tool-window-instance auto-resume map. Managed automatically. |
| `OpenInstanceIds` | `[]` | Tab-persistence list. Managed automatically. |
| `CustomModels` | `[]` | Custom model names you've used (auto-populated). |

---

## 🔨 Building from Source

```powershell
git clone https://github.com/zvirot1/claude-vs2022-plugin.git
cd claude-vs2022-plugin
dotnet build ClaudeCode/ClaudeCode.csproj -c Release
```

Output: `ClaudeCode/bin/Release/net472/ClaudeCode.dll`.

To rebuild the VSIX package (used in releases) the project ships a small PowerShell script (`tools/Build-VSIX.ps1`) that produces a v3-format VSIX (with `manifest.json` + `catalog.json`) compatible with VSIXInstaller 18.x. The standard `Microsoft.VsSDK.targets` doesn't fire under `Microsoft.NET.Sdk` projects, hence the manual packager.

---

## 🗂 Project Layout

```
ClaudeCode/
├─ ClaudeCodePackage.cs       # AsyncPackage entry point, command wiring, tab restore
├─ ClaudeToolWindow.cs        # Multi-instance ToolWindowPane host
├─ Diagnostics.cs             # DIAG logging gate (CLAUDE_DIAG / DiagEnabled)
├─ Cli/                       # Process management + NDJSON protocol
│  ├─ ClaudeCliManager.cs     # spawn/kill/restart, kill-tree on Stop
│  ├─ NdjsonProtocolHandler.cs# system / assistant / stream / result / hook parser
│  └─ CliMessage.cs           # SystemInit, AssistantMessage, StreamEvent,
│                             # SystemNotification, RateLimitEvent, ToolUseSummary…
├─ Model/                     # Conversation state + listener interfaces
│  ├─ ConversationModel.cs    # turn dedup, silent-empty + auto-retry, hash dedup
│  ├─ MessageBlock.cs         # User/Assistant blocks with text + tool segments
│  └─ SessionInfo.cs
├─ Session/                   # Per-project session store
│  ├─ SessionStore.cs         # ~/.claude/claude-sessions/<sid>.json
│  ├─ ClaudeSessionManager.cs # rename / list / delete / track
│  └─ SessionJsonlLoader.cs   # auto-resume history loader
├─ Service/
│  └─ ClaudeProjectService.cs # per-instance DI container
├─ Settings/
│  ├─ ClaudeSettings.cs       # JSON-backed persistent settings
│  └─ ClaudeOptionsPage.cs    # Tools → Options page
├─ Commands/                  # VSCT command handlers (open / send-selection / refactor)
├─ Handlers/                  # SlashCommand, Diff, Checkpoint
├─ Diff/                      # IVsDifferenceService integration
├─ UI/
│  ├─ ClaudeChatPanel.xaml.cs # Main chat panel + WebView2 host + DTE listeners
│  ├─ WebviewBridge.cs        # JSON-message bridge (postMessage + ExecuteScriptAsync)
│  ├─ ClaudeStatusBar.cs      # Footer status (model, session id, tokens, cost)
│  └─ Dialogs/                # SessionHistory, Skills, Memory, Rules, McpServers
└─ Resources/webview/         # HTML + CSS + JS for the chat UI
   ├─ index.html
   ├─ css/chat.css
   └─ js/{app.js, bridge.js, highlight.js}
```

---

## 🐛 Troubleshooting

### "The file is not a valid VSIX package" on install
Make sure you're using a `.vsix` from the GitHub Releases page (built in v3 format with `manifest.json` + `catalog.json`). VSIXInstaller 18.x rejects plain ZIPs. Re-download the latest release.

### Panel shows "Initializing Claude Code..." and never loads
1. Check `%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_*\Extensions\<extension-dir>\claude_debug.log` for errors.
2. Verify `claude --version` works at the command line.
3. If you have multiple Claude windows, each gets its own WebView2 user-data folder under `%TEMP%\ClaudeCode-VS2022-WebView2\inst-N` — try deleting it and reopening.

### Hebrew/Arabic input arrives garbled at the CLI
Already fixed (UTF-8 stdin) — but if you're on a custom build, check that `ClaudeCliManager` wraps `_process.StandardInput.BaseStream` in a `StreamWriter` with `new UTF8Encoding(false)`.

### Stop button stays grey while Agent runs
Already fixed — the button checks `.tool-call.running` in the DOM. If you still see it disabled, clear the WebView2 cache (`%TEMP%\ClaudeCode-VS2022-WebView2\`) and reopen the panel.

### Hook errors (AWS SSO etc.) silently fail
The plugin surfaces these via the `SystemNotification` parser. If you don't see the banner, enable `CLAUDE_DIAG=1` and check `diag.log` for `[DIAG-MSG]` and `[DIAG-STDERR]` lines around the failed turn.

---

## 📜 Changelog

Each major round of fixes is committed with a descriptive message. Recent rounds:

- **Round 7 — IntelliJ parity**: Amazon Q-style Active File chip inside the input frame, blue accent, X-to-dismiss, hide file XML from chat bubble
- **Round 6 — Eclipse parity**: Inline image rendering in user bubbles + click-to-open
- **Round 5 — Eclipse 10 reliability fixes**: send-button latency, late-delta race, system-subtype split, hook errors, silent-empty + auto-retry, friendly error, DIAG flag, MCP global servers, start-race
- **Round 4**: Initial port (multi-instance, Session History, Effort, Tab persistence, Auto-resume, JSONL history loader, content dedup, kill-tree, debounce, rate-limit banner, RTL, UTF-8, custom models, welcome screen)
- **Round 3**: Rename in panel, New Conversation Window, Effort selector
- **Round 2**: Session History dialog with preview + rename
- **Round 1**: Initial port from IntelliJ

For the full list see `git log --oneline`.

---

## 🤝 Credits

Ported from [claude-intellij-plugin](https://github.com/zvirot1/claude-intellij-plugin) and [claude-eclipse-plugin](https://github.com/zvirot1/claude-eclipse-plugin) — same author, same architecture, native VS 2022 implementation.

Built with [Claude Code](https://docs.anthropic.com/claude-code).

## License

MIT — see [LICENSE](LICENSE).
