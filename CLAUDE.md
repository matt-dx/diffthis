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

## Architecture

DiffThis is a Windows desktop app built on **.NET MAUI + Blazor Hybrid**. The MAUI layer is minimal: `MainPage.xaml` hosts a `BlazorWebView`, and all UI is implemented as Razor components in `Components/`. Navigation and routing are handled entirely by Blazor (`NavigationManager`, `@page` directives), not by MAUI Shell.

**Data flow through a diff session:**
1. `Components/Pages/Home.razor` — user picks a repo folder; navigates to `/branches?path=...`
2. `Components/Pages/BranchSelection.razor` — receives `path` via `[SupplyParameterFromQuery]`, calls `IGitService.GetBranchesAsync`, then `GetDiffAsync`; stores result in `DiffSessionService.CurrentDiff`; navigates to `/diff`
3. `Components/Pages/DiffView.razor` — reads `DiffSessionService.CurrentDiff`; renders file sidebar + diff tables; export buttons write files directly to the user's Desktop

`DiffSessionService` is a singleton that acts as a simple cross-page state bus — it exists because Blazor query params can't carry a full diff object.

**Git operations** (`Services/GitService.cs`) run three `git` subprocesses via CliWrap for each diff: `--numstat`, `--name-status`, and `--unified=3`. All use three-dot syntax (`base...compare`) for merge-base comparison. The raw unified diff is parsed in-process into `DiffResult → DiffFile → DiffHunk → DiffLine`.

**Services** are all singletons registered in `MauiProgram.cs`:
- `IGitService` / `GitService` — git subprocess wrapper + unified diff parser
- `ISettingsService` / `SettingsService` — persists theme and recent-repo list via MAUI `Preferences` API
- `IExportService` / `ExportService` — generates Markdown and styled HTML from a `DiffResult`
- `DiffSessionService` — cross-page state (no interface; injected directly)

**JS interop**: `wwwroot/app.js` exposes a single function `scrollToElement(id)` used by `DiffView.razor` to scroll the diff panel when a file is selected in the sidebar.

The `ViewModels/` and `Views/` directories exist on disk but are not yet wired up — current Razor pages use inline `@code` blocks and inject services directly.
