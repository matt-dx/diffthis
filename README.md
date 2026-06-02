# DiffThis

A Windows desktop diff tool for comparing git branches, with AI-powered code review and explanation.

Built on **.NET MAUI + Blazor Hybrid** targeting `net10.0-windows10.0.19041.0`.

## Features

- Compare any two branches (or pinned commits) in a local git repository
- Syntax-highlighted unified diff with collapsible file sections
- AI review and explanation via the `claude` CLI (requires Claude Code to be installed and authenticated)
- Analysis link navigation — AI references to `File.cs:42` are clickable and scroll the diff to that line
- Markdown export of the full diff
- Per-repo branch selection memory, light/dark theme, font ligature toggle

## Requirements

- Windows 10 (19041+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with MAUI workload
- `git` on PATH
- [Claude Code CLI](https://claude.ai/code) authenticated (`claude auth login`) — optional, for AI features

## Build & Run

```bash
# Debug
dotnet build -f net10.0-windows10.0.19041.0

# Release
dotnet build -f net10.0-windows10.0.19041.0 -c Release

# Run
dotnet run -f net10.0-windows10.0.19041.0
```

The target framework must always be specified explicitly. In debug builds, Blazor DevTools are accessible via F12.

## Project Structure

```text
DiffThis/
├── MauiProgram.cs              DI registration; DevTools in debug
├── MainPage.xaml               Hosts the BlazorWebView (minimal MAUI shell)
├── Components/
│   ├── Pages/
│   │   ├── Home.razor          Repo folder picker; navigates to /branches
│   │   ├── BranchSelection.razor   Branch + commit-pin pickers; triggers diff
│   │   └── MainView.razor      Side-by-side / tabbed layout for diff + analysis
│   ├── Panels/
│   │   ├── DiffPanel.razor     File sidebar + collapsible diff tables + syntax highlighting
│   │   └── AnalysisPanel.razor AI result cards (explain / review); analysis link rendering
│   └── Layout/
├── Models/                     DiffResult · DiffFile · DiffHunk · DiffLine · BranchSelectionState
├── Services/
│   ├── GitService              CliWrap → git diff/branch/log; unified-diff parser
│   ├── SettingsService         Preferences API (theme, ligatures, recent repos, branch state, AI config)
│   ├── ExportService           Markdown generation from DiffResult
│   ├── ClaudeService           Invokes claude CLI subprocess with diff on stdin
│   ├── ClaudeAuthService       Reads ~/.claude/.credentials.json; resolves claude executable
│   ├── ClaudeModelService      Fetches /v1/models; sorts by tier; persists to Preferences
│   ├── PromptService           Loads review.md / explain.md (embedded or user override)
│   ├── AnalysisLinkService     Parses AI markdown for file refs; fires FocusRequested events
│   ├── AiCacheService          LRU cache of AI results keyed by diff + run config
│   ├── DiffSessionService      Singleton state bus passing DiffResult between pages
│   └── SyntaxHighlighter       ColorCode wrapper with custom language definitions
├── Prompts/
│   ├── review.md               Default review prompt template
│   └── explain.md              Default explain prompt template
└── wwwroot/
    ├── app.css                 CSS variables for syntax token colours (light/dark)
    └── app.js                  JS interop: scrollToElement, copyToClipboard, analysis link callback
```

## AI Prompts

Custom prompt templates can be placed at `%LOCALAPPDATA%\DiffThis\prompts\review.md` or `explain.md`. Use `{{Variable}}` placeholders — the built-in templates in `Prompts/` show available variables.

## AI Result Caching

AI results are cached at `%LOCALAPPDATA%\DiffThis\ai-cache.json` (max 500 entries, LRU-evicted). The cache key includes repo path, base/compare refs, feature, model, tools-enabled flag, and max-turns — changing any of these produces a fresh run.
