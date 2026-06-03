# DiffThis

A Windows desktop diff tool for comparing git branches, with AI-powered code review and explanation.

Built on **.NET MAUI + Blazor Hybrid** targeting `net10.0-windows10.0.19041.0`.

## Features

- Compare any two branches (or pinned commits) in a local git repository
- Syntax-highlighted unified diff with collapsible file sections
- AI review and explanation via **Claude** (Claude Code CLI) or **GitHub Copilot**
- Analysis link navigation — AI references to `File.cs:42` are clickable and scroll the diff to that line
- Markdown export of the full diff
- Per-repo branch selection memory, light/dark theme, font ligature toggle

## Requirements

- Windows 10 (19041+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with MAUI workload
- `git` on PATH
- For Claude: [Claude Code CLI](https://claude.ai/code) authenticated (`claude auth login`)
- For GitHub Copilot: a GitHub account with Copilot access (sign-in handled in-app)

## Build & Run

```bash
# Debug
dotnet build DiffThis\DiffThis.csproj -f net10.0-windows10.0.19041.0

# Release
dotnet build DiffThis\DiffThis.csproj -f net10.0-windows10.0.19041.0 -c Release

# Run
dotnet run --project DiffThis\DiffThis.csproj -f net10.0-windows10.0.19041.0

# Tests
dotnet test DiffThis.AI.OpenAI.Tests\DiffThis.AI.OpenAI.Tests.csproj
```

The target framework must always be specified explicitly for the MAUI project. In debug builds, Blazor DevTools are accessible via F12.

## Project Structure

```text
DiffThis/                       MAUI app host (entry point)
DiffThis.UI/                    Razor components, pages, panels
DiffThis.Core/                  Domain models (DiffResult, DiffFile, DiffHunk, DiffLine)
DiffThis.AI.Shared/             PromptService, AiCacheService, AnalysisLinkService, DiffSessionService
DiffThis.AI.Claude/             Claude CLI integration
DiffThis.AI.OpenAI/             GitHub Copilot integration (OpenAI-compatible API)
DiffThis.AI.OpenAI.Tests/       Integration tests for Copilot services
```

Key files:

```text
DiffThis/
├── MauiProgram.cs              DI registration; DevTools in debug
└── MainPage.xaml               Hosts the BlazorWebView

DiffThis.UI/Components/Pages/
├── Home.razor                  Repo folder picker
├── BranchSelection.razor       Branch + commit-pin pickers; triggers diff
├── MainView.razor              Side-by-side / tabbed layout for diff + analysis
└── Settings.razor              App settings

DiffThis.UI/Components/Panels/
├── DiffPanel.razor             File sidebar + collapsible diff tables + syntax highlighting
└── AnalysisPanel.razor         AI result cards; analysis link rendering

DiffThis.UI/wwwroot/
├── app.css                     CSS variables for syntax token colours (light/dark)
└── app.js                      JS interop: scrollToElement, copyToClipboard, analysis link callback
```

## AI Providers

### Claude

Uses the `claude` CLI as a subprocess (diff passed on stdin). Requires [Claude Code](https://claude.ai/code) to be installed and authenticated via `claude auth login`. Model list is fetched from the Anthropic API using your CLI credentials.

### GitHub Copilot

Calls the GitHub Copilot chat completions API directly. Sign in with your GitHub account from the Settings page — DiffThis handles the device-code OAuth flow and session token refresh automatically. Requires a GitHub account with Copilot access.

## AI Prompts

Custom prompt templates can be placed at `%LOCALAPPDATA%\DiffThis\prompts\review.md` or `explain.md`. Use `{{Variable}}` placeholders — the built-in templates in `DiffThis.AI.Shared/Prompts/` show available variables.

## AI Result Caching

AI results are cached at `%LOCALAPPDATA%\DiffThis\ai-cache.json` (max 500 entries, LRU-evicted). The cache key includes repo path, base/compare refs, feature, model, tools-enabled flag, and max-turns — changing any of these produces a fresh run.
