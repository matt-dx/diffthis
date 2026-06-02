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
3. `Components/Pages/MainView.razor` — reads `DiffSessionService.CurrentDiff`; hosts `DiffPanel` and `AnalysisPanel` in either a side-by-side or tabbed layout (both panels stay mounted in tab mode — hidden via CSS — to preserve state); Export Markdown writes to the user's Desktop

`DiffSessionService` is a singleton state bus — it exists because Blazor query params can't carry a full diff object.

**Panel components** (`Components/Panels/`):

- `DiffPanel.razor` — renders the file sidebar + collapsible diff tables; runs `SyntaxHighlighter` on each hunk at load time; receives `PendingFocus` from `MainView` to scroll/highlight a specific file/line
- `AnalysisPanel.razor` — shows AI results (explain + review) as cards, one per model run; lets users add, refresh, hide, and delete results; renders AI markdown via Markdig and post-processes it for analysis links (see below)

**Git operations** (`Services/GitService.cs`) run three `git` subprocesses via CliWrap for each diff: `--numstat`, `--name-status`, and `--unified=3`. All use two-dot syntax (`base..compare`). The raw unified diff is parsed in-process into `DiffResult → DiffFile → DiffHunk → DiffLine`. A fourth subprocess (`git log`) is used for commit pinning.

**Syntax highlighting** (`Services/SyntaxHighlighter.cs`) wraps the ColorCode library using `HtmlClassFormatter` so that token colours are controlled entirely by CSS variables in `app.css` — giving free light/dark theme support. `GetLanguage` maps file extensions to `ILanguage` instances; several languages not built into ColorCode are implemented as inline `ILanguage` classes (Go, Rust, YAML, Bash, TOML, Dockerfile, T-SQL). C#, TypeScript, and JavaScript get two extra rules appended: method-call highlighting and PascalCase type highlighting. `HighlightLines` joins a hunk's lines, runs ColorCode over the whole block (to preserve cross-line token context), then splits the result back per-line while correctly closing/reopening any spans that straddle a newline.

> **Note:** `SyntaxHighlighter.GetLanguage` and `HighlightLines` currently write a debug log to `~/Desktop/hl-debug.txt`. This is a temporary debugging aid.

**AI integration** (`Services/ClaudeService.cs`): DiffThis never calls the Anthropic API directly. Instead it invokes the `claude` CLI as a subprocess, passing the diff as stdin and using `--output-format text`. Prompts are built by `PromptService`, which loads `review.md` / `explain.md` from embedded resources and renders `{{Variable}}` placeholders; users can override either template by placing a file at `%LOCALAPPDATA%\DiffThis\prompts\{name}.md`. The diff content passed to the prompt is capped at 60,000 characters and truncated if longer. `ClaudeAuthService` reads credentials from `~/.claude/.credentials.json` (written by the Claude CLI after `claude auth login`) and resolves the `claude` executable from well-known paths and `PATH`. Auth state is surfaced in the UI; actual OAuth token refresh is handled transparently by the CLI subprocess. Results are cached per `(repoPath, baseRef, compareRef, feature, model, toolsEnabled, maxTurns)` by `AiCacheService`, which persists to `%LOCALAPPDATA%\DiffThis\ai-cache.json` (max 500 entries, LRU-evicted to 400). The cache key type `AiRunKey` identifies a run configuration.

**Analysis links**: `AnalysisPanel` post-processes rendered Markdown to make file references (e.g. `Services/GitService.cs:42`) clickable. `AnalysisLinkService` parses AI output for file/line references using regex, resolves them to diff file indices, and builds a line-level index. When a user clicks a reference, `AnalysisLinkService.RequestFocus` fires `FocusRequested`, which `MainView` catches and forwards to `DiffPanel` via `PendingFocus`. The link is dispatched from JS to .NET via a `DotNetObjectReference` registered as `setAnalysisRefDotNet`. References are classified by the `##` heading they appear under (Bug, LogicError, Security, Other) and displayed as coloured indicators in the diff sidebar.

**Services** (all singletons, registered in `MauiProgram.cs`):

- `IGitService` / `GitService` — git subprocess wrapper + unified diff parser
- `ISettingsService` / `SettingsService` — persists theme, font-ligatures toggle, recent-repo list, per-repo branch selection state, and AI model/config preferences via MAUI `Preferences` API
- `IExportService` / `ExportService` — generates Markdown from a `DiffResult` and writes it to a file
- `IClaudeService` / `ClaudeService` — invokes `claude` CLI subprocess for diff review and explanation
- `IClaudeAuthService` / `ClaudeAuthService` — reads `~/.claude/.credentials.json`; resolves claude executable path; exposes auth state and access token
- `IClaudeModelService` / `ClaudeModelService` — fetches available models from `GET /v1/models` using the auth token; sorts by tier (opus → sonnet → haiku) then version; persists to `Preferences`; fires `ModelsChanged` event; auto-refreshes on first run; supports custom display names
- `PromptService` — loads and renders prompt templates (embedded resources or user overrides)
- `IAnalysisLinkService` / `AnalysisLinkService` — parses AI markdown for file references, maps them to diff positions, fires `FocusRequested` events
- `AiCacheService` — persists AI responses keyed by diff + run config; no interface (injected directly)
- `DiffSessionService` — cross-page state (no interface; injected directly)
- `SyntaxHighlighter` — static class, no registration needed

**JS interop**: `wwwroot/app.js` exposes `scrollToElement(id)` (used by `MainView` / `AnalysisPanel` to scroll panels), `copyToClipboard(text)`, and `setAnalysisRefDotNet` / `clearAnalysisRefDotNet` (registers the .NET callback for analysis link clicks).

**Model notes:**

- `DiffResult` carries both raw branch/commit refs (`BaseBranch`, `CompareBranch`) and human-readable display labels (`BaseLabel`, `CompareLabel`) — use `BaseDisplay` / `CompareDisplay` computed properties in the UI, as they fall back to the raw ref when no label is set.
- `BranchSelectionState` is persisted by `SettingsService` keyed on repo path, allowing branch + commit-pin selections to survive restarts.

The `ViewModels/` and `Views/` directories exist on disk but are not wired up — Razor pages use inline `@code` blocks and inject services directly.
