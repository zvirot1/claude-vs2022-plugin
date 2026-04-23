# Claude Code for Visual Studio 2022

A Visual Studio 2022 extension that integrates [Claude Code](https://docs.anthropic.com/claude-code) into the IDE as a chat panel, porting feature parity from the official IntelliJ and Eclipse plugins.

## Features

- **Chat panel** (View → Other Windows → Claude Code) with WebView2-based UI
- **Multi-instance** — open several independent Claude windows via the 📋 button
- **Per-tab CLI isolation** — each window runs its own Claude CLI process with its own context
- **Session History** — 4-column table + preview + rename + resume
- **Rename session** from panel header (click the title)
- **Effort / thinking-budget** selector (Auto / Low / Medium / High / Max) with hot-swap
- **Permission mode** selector (Ask / Auto / Plan)
- **Model selector** — click the model badge to switch (sonnet / opus / haiku / custom)
- **Send debounce**, **Stop = kill process tree**, **Stream timeout** suppression
- **Auto-resume on reopen** — VS restart restores last session + history from JSONL
- **Tab persistence** — previously-open windows are restored on next launch
- **Rate-limit banner** with countdown
- **RTL + UTF-8** — Hebrew/Arabic input is right-aligned and encoded correctly to the CLI
- **Ctrl+V image paste**, **Enter / Shift-Enter** newline behavior

## Installation

1. Download `ClaudeCode.vsix`
2. Double-click to install into Visual Studio 2022 (17.0+)
3. Ensure the Claude CLI is installed: `npm install -g @anthropic-ai/claude-code`
4. Open a solution → View → Other Windows → Claude Code

## Configuration

Settings live at `%LOCALAPPDATA%\ClaudeCode\settings.json`:
- `CliPath` — optional explicit path to `claude.exe`
- `SelectedModel` — `default`, `sonnet`, `opus`, `haiku`, or a full model id
- `EffortLevel` — `auto` / `low` / `medium` / `high` / `max`
- `InitialPermissionMode` — `default` / `acceptEdits` / `bypassPermissions` / `plan`

## Building from source

```
dotnet build ClaudeCode/ClaudeCode.csproj -c Release
```

DLL lands in `ClaudeCode/bin/Release/net472/ClaudeCode.dll`.

To rebuild the VSIX package, use the `Build-VSIX.ps1` script or open in VS and build the solution.

## Project layout

```
ClaudeCode/
├─ ClaudeCodePackage.cs       # AsyncPackage entry point
├─ ClaudeToolWindow.cs        # Tool window host
├─ Cli/                       # CLI process management + NDJSON protocol
├─ Model/                     # ConversationModel, MessageBlock, listeners
├─ Session/                   # SessionStore + JSONL history loader
├─ Service/                   # ClaudeProjectService (per-instance DI container)
├─ Settings/                  # Persistent settings + Options page
├─ UI/                        # WPF chat panel + dialogs
├─ Commands/                  # VSCT command handlers
├─ Handlers/                  # Diff / slash-command / checkpoint handlers
├─ Diff/                      # Diff rendering via IVsDifferenceService
└─ Resources/webview/         # HTML + CSS + JS for the chat UI
```

## Credits

Ported from [claude-intellij-plugin](https://github.com/anthropics/claude-intellij-plugin) and [claude-eclipse-plugin](https://github.com/anthropics/claude-eclipse-plugin).
