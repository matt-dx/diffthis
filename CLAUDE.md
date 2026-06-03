# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build (debug) — must target the MAUI entry-point project
dotnet build DiffThis\DiffThis.csproj -f net10.0-windows10.0.19041.0

# Build (release)
dotnet build DiffThis\DiffThis.csproj -f net10.0-windows10.0.19041.0 -c Release

# Run
dotnet run --project DiffThis\DiffThis.csproj -f net10.0-windows10.0.19041.0

# Run tests (OpenAI/Copilot integration tests only)
dotnet test DiffThis.AI.OpenAI.Tests\DiffThis.AI.OpenAI.Tests.csproj
```

The MAUI entry-point project (`DiffThis\DiffThis.csproj`) is Windows-only; its target framework must always be specified explicitly as `net10.0-windows10.0.19041.0`. Non-MAUI projects (`DiffThis.Core`, `DiffThis.AI.*`) can be built without the framework flag.

**Solution structure:**
- `DiffThis/` — MAUI app host (entry point, `MauiProgram.cs`, `MainPage.xaml`)
- `DiffThis.UI/` — Razor components, pages, panels
- `DiffThis.Core/` — domain models (`DiffResult`, `DiffFile`, etc.), shared interfaces
- `DiffThis.AI.Shared/` — `PromptService`, `AiCacheService`, `AnalysisLinkService`, `DiffSessionService`
- `DiffThis.AI.Claude/` — Claude CLI integration (`ClaudeService`, `ClaudeAuthService`, `ClaudeModelService`)
- `DiffThis.AI.OpenAI/` — GitHub Copilot integration (`CopilotService`, `CopilotAuthService`, `CopilotModelService`)
- `DiffThis.AI.OpenAI.Tests/` — integration tests for Copilot services

In debug builds, the Blazor WebView exposes DevTools (F12) via `MauiProgram.cs`.

## Architecture

DiffThis is a Windows desktop app built on **.NET MAUI + Blazor Hybrid**. The MAUI layer is minimal: `MainPage.xaml` hosts a `BlazorWebView`, and all UI is implemented as Razor components in `Components/`. Navigation and routing are handled entirely by Blazor (`NavigationManager`, `@page` directives), not by MAUI Shell.

**Data flow through a diff session:**

1. `Components/Pages/Home.razor` — user picks a repo folder; navigates to `/branches?path=...`
2. `Components/Pages/BranchSelection.razor` — receives `path` via `[SupplyParameterFromQuery]`; calls `IGitService.GetBranchesAsync`; supports optionally pinning either side to a specific commit via a second `GetCommitsAsync` call; saves/restores selections per repo path via `ISettingsService.GetBranchState` / `SaveBranchState`; calls `GetDiffAsync` and stores result in `DiffSessionService.CurrentDiff`; navigates to `/diff`
3. `Components/Pages/MainView.razor` — reads `DiffSessionService.CurrentDiff`; hosts `DiffPanel` and `AnalysisPanel` in either a side-by-side or tabbed layout (both panels stay mounted in tab mode — hidden via CSS — to preserve state); Export Markdown writes to the user's Desktop
4. `Components/Pages/Settings.razor` — app settings page (theme, AI provider config, prompt overrides, etc.)

`DiffSessionService` is a singleton state bus — it exists because Blazor query params can't carry a full diff object.

**Panel components** (`Components/Panels/`):

- `DiffPanel.razor` — renders the file sidebar + collapsible diff tables; runs `SyntaxHighlighter` on each hunk at load time; receives `PendingFocus` from `MainView` to scroll/highlight a specific file/line
- `AnalysisPanel.razor` — shows AI results (explain + review) as cards, one per model run; lets users add, refresh, hide, and delete results; renders AI markdown via Markdig and post-processes it for analysis links (see below)

**Git operations** (`Services/GitService.cs`) run three `git` subprocesses via CliWrap for each diff: `--numstat`, `--name-status`, and `--unified=3`. All use two-dot syntax (`base..compare`). The raw unified diff is parsed in-process into `DiffResult → DiffFile → DiffHunk → DiffLine`. A fourth subprocess (`git log`) is used for commit pinning.

**Syntax highlighting** (`Services/SyntaxHighlighter.cs`) wraps the ColorCode library using `HtmlClassFormatter` so that token colours are controlled entirely by CSS variables in `app.css` — giving free light/dark theme support. `GetLanguage` maps file extensions to `ILanguage` instances; several languages not built into ColorCode are implemented as inline `ILanguage` classes (Go, Rust, YAML, Bash, TOML, Dockerfile, T-SQL). C#, TypeScript, and JavaScript get two extra rules appended: method-call highlighting and PascalCase type highlighting. `HighlightLines` joins a hunk's lines, runs ColorCode over the whole block (to preserve cross-line token context), then splits the result back per-line while correctly closing/reopening any spans that straddle a newline.

> **Note:** `SyntaxHighlighter.GetLanguage` and `HighlightLines` currently write a debug log to `~/Desktop/hl-debug.txt`. This is a temporary debugging aid.

**AI integration** — DiffThis supports two AI providers, both sharing the same `PromptService`, `AiCacheService`, and `AnalysisLinkService` plumbing.

*Claude* (`Services/ClaudeService.cs`): invokes the `claude` CLI as a subprocess, passing the diff as stdin and using `--output-format text`. `ClaudeAuthService` reads credentials from `~/.claude/.credentials.json` (written by the Claude CLI after `claude auth login`) and resolves the `claude` executable from well-known paths and `PATH`; actual OAuth token refresh is handled transparently by the CLI subprocess. `ClaudeModelService` fetches available models from `GET /v1/models` using the auth token, sorts by tier (opus → sonnet → haiku) then version, persists to `Preferences`, and fires `ModelsChanged`.

*GitHub Copilot* (`DiffThis.AI.OpenAI/Services/`): `CopilotService` calls `https://api.githubcopilot.com/chat/completions` directly (OpenAI-compatible API). `CopilotAuthService` drives a device-code OAuth flow against GitHub (client ID `Iv1.b507a08c87ecfe98`, same as VS Code / copilot.vim), exchanges the OAuth token for short-lived Copilot session tokens via `api.github.com/copilot_internal/v2/token`, and stores credentials in SecureStorage. `CopilotModelService` fetches models from `https://api.githubcopilot.com/models`, filtering for chat-capable entries; falls back to a hardcoded list (gpt-4o, o1, o3-mini, claude-3.7-sonnet, gemini-2.0-flash, etc.) when the API is unavailable.

Prompts are built by `PromptService`, which loads `review.md` / `explain.md` from embedded resources and renders `{{Variable}}` placeholders; users can override either template by placing a file at `%LOCALAPPDATA%\DiffThis\prompts\{name}.md`. The diff content is capped at 60,000 characters and truncated if longer. Available placeholders: `{{RepositoryName}}`, `{{BaseDisplay}}`, `{{CompareDisplay}}`, `{{FileCount}}`, `{{Additions}}`, `{{Deletions}}`, `{{FileList}}` (changed files with status + detected language), `{{DiffContent}}`. Results are cached per `(repoPath, baseRef, compareRef, feature, model, toolsEnabled, maxTurns)` by `AiCacheService`, which persists to `%LOCALAPPDATA%\DiffThis\ai-cache.json` (max 500 entries, LRU-evicted to 400). The cache key type `AiRunKey` identifies a run configuration.

**Analysis links**: `AnalysisPanel` post-processes rendered Markdown to make file references (e.g. `Services/GitService.cs:42`) clickable. `AnalysisLinkService` parses AI output for file/line references using regex, resolves them to diff file indices, and builds a line-level index. Line references whose line number falls outside every hunk range in the file are treated as hallucinated and are excluded from the scroll index (but still show a file-level indicator). When a user clicks a reference, `AnalysisLinkService.RequestFocus` fires `FocusRequested`, which `MainView` catches and forwards to `DiffPanel` via `PendingFocus`. The link is dispatched from JS to .NET via a `DotNetObjectReference` registered as `setAnalysisRefDotNet`. References are classified by the `##` heading they appear under (Bug, LogicError, Security, Other) and carry a `RefSeverity` (Critical/High/Medium/Low/Unknown) detected from keywords in the surrounding text, falling back to a category-based default (Security/Bug → High, LogicError → Medium, Other → Low). Severity is shown in the indicator tooltip and used to modulate indicator opacity in the diff sidebar.

**Services** (all singletons, registered in `MauiProgram.cs`):

- `IGitService` / `GitService` — git subprocess wrapper + unified diff parser; `GetDiffAsync` accepts a `contextLines` parameter (default 3) passed as `--unified=N` to `git diff`
- `ISettingsService` / `SettingsService` — persists theme, font-ligatures toggle, recent-repo list, per-repo branch selection state, AI model/config preferences, and `DiffContextLines` (3/10/25/50) via MAUI `Preferences` API
- `IExportService` / `ExportService` — generates Markdown from a `DiffResult` and writes it to a file
- `IClaudeService` / `ClaudeService` — invokes `claude` CLI subprocess for diff review and explanation
- `IClaudeAuthService` / `ClaudeAuthService` — reads `~/.claude/.credentials.json`; resolves claude executable path; exposes auth state and access token
- `IClaudeModelService` / `ClaudeModelService` — fetches available models from `GET /v1/models`; persists to `Preferences`; fires `ModelsChanged`
- `ICopilotService` / `CopilotService` — calls GitHub Copilot chat completions API for diff review/explanation
- `ICopilotAuthService` / `CopilotAuthService` — device-code OAuth for GitHub Copilot; stores credentials in SecureStorage; manages session token refresh
- `ICopilotModelService` / `CopilotModelService` — fetches and filters Copilot chat models; hardcoded fallback list
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
