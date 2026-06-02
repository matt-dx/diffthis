# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build (debug)
dotnet build -f net10.0-windows10.0.19041.0

# Build (release)
dotnet build -f net10.0-windows10.0.19041.0 -c Release

# Run
dotnet run -f net10.0-windows10.0.19041.0
```

There are no tests in this project. Target framework must always be specified explicitly — the project is Windows-only (`net10.0-windows10.0.19041.0`).

In debug builds, the Blazor WebView exposes DevTools (F12) via `MauiProgram.cs`.

## Architecture

DiffThis is a Windows desktop app built on **.NET MAUI + Blazor Hybrid**. The MAUI layer is minimal: `MainPage.xaml` hosts a `BlazorWebView`, and all UI is implemented as Razor components in `Components/`. Navigation and routing are handled entirely by Blazor (`NavigationManager`, `@page` directives), not by MAUI Shell.

**Data flow through a diff session:**

1. `Components/Pages/Home.razor` — user picks a repo folder; navigates to `/branches?path=...`
2. `Components/Pages/BranchSelection.razor` — receives `path` via `[SupplyParameterFromQuery]`; calls `IGitService.GetBranchesAsync`; supports optionally pinning either side to a specific commit via a second `GetCommitsAsync` call; saves/restores selections per repo path via `ISettingsService.GetBranchState` / `SaveBranchState`; calls `GetDiffAsync` and stores result in `DiffSessionService.CurrentDiff`; navigates to `/diff`
3. `Components/Pages/DiffView.razor` — reads `DiffSessionService.CurrentDiff`; runs `SyntaxHighlighter` on each hunk at load time; renders file sidebar + collapsible diff tables; Export Markdown writes to the user's Desktop

`DiffSessionService` is a singleton state bus — it exists because Blazor query params can't carry a full diff object.

**Git operations** (`Services/GitService.cs`) run three `git` subprocesses via CliWrap for each diff: `--numstat`, `--name-status`, and `--unified=3`. All use two-dot syntax (`base..compare`). The raw unified diff is parsed in-process into `DiffResult → DiffFile → DiffHunk → DiffLine`. A fourth subprocess (`git log`) is used for commit pinning.

**Syntax highlighting** (`Services/SyntaxHighlighter.cs`) wraps the ColorCode library using `HtmlClassFormatter` so that token colours are controlled entirely by CSS variables in `app.css` — giving free light/dark theme support. `GetLanguage` maps file extensions to `ILanguage` instances; several languages not built into ColorCode are implemented as inline `ILanguage` classes (Go, Rust, YAML, Bash, TOML, Dockerfile, T-SQL). C#, TypeScript, and JavaScript get two extra rules appended: method-call highlighting and PascalCase type highlighting. `HighlightLines` joins a hunk's lines, runs ColorCode over the whole block (to preserve cross-line token context), then splits the result back per-line while correctly closing/reopening any spans that straddle a newline.

> **Note:** `SyntaxHighlighter.GetLanguage` and `HighlightLines` currently write a debug log to `~/Desktop/hl-debug.txt`. This is a temporary debugging aid.

**AI integration** (`Services/ClaudeService.cs`): DiffThis never calls the Anthropic API directly. Instead it invokes the `claude` CLI as a subprocess, passing the diff as stdin and using `--output-format text`. `ClaudeAuthService` reads credentials from `~/.claude/.credentials.json` (written by the Claude CLI after `claude auth login`) and resolves the `claude` executable from well-known paths and `PATH`. Auth state is surfaced in the UI; actual OAuth token refresh is handled transparently by the CLI subprocess. Results are cached per `(repoPath, baseRef, compareRef, feature, model, toolsEnabled, maxTurns)` by `AiCacheService`, which persists to `%LOCALAPPDATA%\DiffThis\ai-cache.json` (max 500 entries, LRU-evicted to 400). The cache key type `AiRunKey` identifies a run configuration; multiple cached results for the same diff are shown as tabs in the UI.

**Services** (all singletons, registered in `MauiProgram.cs`):

- `IGitService` / `GitService` — git subprocess wrapper + unified diff parser
- `ISettingsService` / `SettingsService` — persists theme, font-ligatures toggle, recent-repo list, per-repo branch selection state, and AI model/config preferences via MAUI `Preferences` API
- `IExportService` / `ExportService` — generates Markdown from a `DiffResult` and writes it to a file
- `IClaudeService` / `ClaudeService` — invokes `claude` CLI subprocess for diff review and explanation; streams JSON output
- `IClaudeAuthService` / `ClaudeAuthService` — reads `~/.claude/.credentials.json`; resolves claude executable path; exposes auth state and email
- `AiCacheService` — persists AI responses keyed by diff + run config; no interface (injected directly)
- `DiffSessionService` — cross-page state (no interface; injected directly)
- `SyntaxHighlighter` — static class, no registration needed

**JS interop**: `wwwroot/app.js` exposes `scrollToElement(id)`, used by `DiffView.razor` to scroll the diff panel when a file is selected in the sidebar.

**Model notes:**

- `DiffResult` carries both raw branch/commit refs (`BaseBranch`, `CompareBranch`) and human-readable display labels (`BaseLabel`, `CompareLabel`) — use `BaseDisplay` / `CompareDisplay` computed properties in the UI, as they fall back to the raw ref when no label is set.
- `BranchSelectionState` is persisted by `SettingsService` keyed on repo path, allowing branch + commit-pin selections to survive restarts.

The `ViewModels/` and `Views/` directories exist on disk but are not wired up — Razor pages use inline `@code` blocks and inject services directly.
