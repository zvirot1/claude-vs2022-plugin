# Feature Gap Analysis: IntelliJ Plugin vs VS 2022 Extension

## ✅ Fully Implemented (Matching IntelliJ)
1. Chat panel with WebView2 (JCEF equivalent)
2. CLI process management (start/stop/restart/health monitor)
3. NDJSON protocol parsing (all message types)
4. ConversationModel with streaming support
5. Permission handling (Accept/Reject/Always Allow)
6. Tool call rendering (running/completed/failed)
7. Extended thinking indicator
8. Slash commands (all 19: 13 local + 6 CLI-forwarded)
9. File attachment manager
10. Session management (create/resume/persist/history)
11. Checkpoint/revert system
12. Edit decision manager (accept/reject edits)
13. Theme support (light/dark auto-detection)
14. Status bar (connection, model, tokens, cost)
15. Settings persistence (JSON file)
16. WebView bridge (C#↔JS bidirectional)
17. Webview UI (all HTML/CSS/JS from IntelliJ)
18. Preferences dialog (CLI path, model, ctrl+enter)

## ⚠️ Partial / Missing Features

### 1. Editor Context Menu (PARTIAL)
- **IntelliJ**: Right-click → Claude Code submenu with 5 actions
- **VS 2022**: Commands registered but NOT in right-click menu (CommandBars approach only adds to Tools menu)
- **Gap**: Need to add context menu items to editor right-click

### 2. Keyboard Shortcuts (PARTIAL)
- **IntelliJ**: Ctrl+Shift+C, Ctrl+Shift+S, Ctrl+Shift+R, Alt+K, Ctrl+Escape
- **VS 2022**: Defined in VSCT but not functional (VSCT not loading)
- **Gap**: Implement keyboard bindings via DTE Commands or IVsFilterKeys

### 3. @-Mention with Line Range (PARTIAL)
- **IntelliJ**: Alt+K inserts current file + selection as @-mention with line range
- **VS 2022**: File search works, but no Alt+K shortcut to auto-insert current file
- **Gap**: Implement InsertAtMention action

### 4. Rules Dialog (MISSING)
- **IntelliJ**: Full dialog for editing CLAUDE.md, .claude.local.md, permissions
- **VS 2022**: Forwards /rules to CLI (no native dialog)
- **Gap**: Low priority — CLI handles it

### 5. MCP Servers Dialog (MISSING)
- **IntelliJ**: Full dialog for editing .mcp.json with table UI
- **VS 2022**: Forwards /mcp to CLI
- **Gap**: Low priority — CLI handles it

### 6. Hooks Dialog (MISSING)
- **IntelliJ**: Full dialog for hook configuration
- **VS 2022**: Forwards /hooks to CLI
- **Gap**: Low priority — CLI handles it

### 7. Memory Dialog (MISSING)
- **IntelliJ**: Editor for MEMORY.md with tips
- **VS 2022**: Forwards /memory to CLI
- **Gap**: Low priority — CLI handles it

### 8. Skills Dialog (MISSING)
- **IntelliJ**: Full plugin browser with marketplace
- **VS 2022**: Forwards /skills to CLI
- **Gap**: Low priority — CLI handles it

### 9. Session History Dialog (MISSING)
- **IntelliJ**: Full dialog with table, search, sort, details panel
- **VS 2022**: Text-based /history output in chat
- **Gap**: Medium priority — could improve UX

### 10. Diff Viewer Dialog (MISSING)
- **IntelliJ**: Side-by-side diff using IntelliJ's built-in diff viewer
- **VS 2022**: Edit accept/reject works but no visual diff dialog
- **Gap**: Medium priority — could use VS diff API

### 11. VS Options Page (NOT WORKING)
- **IntelliJ**: Full settings page in IDE preferences
- **VS 2022**: pkgdef registration not loading (manual deploy issue)
- **Gap**: Workaround: Preferences dialog via ⚙ button works

### 12. Status Bar Widget in VS Footer (MISSING)
- **IntelliJ**: Custom widget in IDE status bar (bottom right)
- **VS 2022**: Status shown only inside the Claude panel, not in VS main status bar
- **Gap**: Low priority — panel status bar sufficient

### 13. Auto-save Before Tools (PARTIAL)
- **IntelliJ**: Setting to auto-save dirty editors before tool execution
- **VS 2022**: Setting exists but not wired to VS DTE save
- **Gap**: Wire to DTE.Documents.SaveAll()

### 14. New Tab / Parallel Conversations (MISSING)
- **IntelliJ**: Multiple chat tabs in single tool window
- **VS 2022**: Noted as unsupported
- **Gap**: Could implement with multiple tool window instances
