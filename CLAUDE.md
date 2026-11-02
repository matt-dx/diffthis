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
- `DiffThis.Core/` — domain models (`DiffResult`, `DiffFile`, etc.), shared interfaces, `GitService`, `SettingsService`, `LogService`, and pull-request support (`PullRequestService`, `GitHostService`, the per-provider `*AuthService` classes, `OAuthLoopback`)
- `DiffThis.AI.Shared/` — `PromptService`, `AiCacheService`, `AnalysisLinkService`, `DiffSessionService`
- `DiffThis.AI.Claude/` — Claude CLI integration (`ClaudeService`, `ClaudeAuthService`, `ClaudeModelService`)
- `DiffThis.AI.OpenAI/` — GitHub Copilot, OpenAI/Codex, and Ollama integration (`CopilotService`, `CopilotAuthService`, `CopilotModelService`, `OpenAiService`, `OpenAiAuthService`, `OpenAiModelService`, `OllamaService`, `OllamaEndpointService`)
- `DiffThis.AI.OpenAI.Tests/` — integration tests for Copilot services

In debug builds, the Blazor WebView exposes DevTools (F12) via `MauiProgram.cs`.

## Architecture

DiffThis is a Windows desktop app built on **.NET MAUI + Blazor Hybrid**. The MAUI layer is minimal: `MainPage.xaml` hosts a `BlazorWebView`, and all UI is implemented as Razor components in `Components/`. Navigation and routing are handled entirely by Blazor (`NavigationManager`, `@page` directives), not by MAUI Shell.

**Data flow through a diff session:**

1. `Components/Pages/Home.razor` — user picks a repo folder; navigates to `/branches?path=...`
2. `Components/Pages/BranchSelection.razor` — receives `path` via `[SupplyParameterFromQuery]`; calls `IGitService.GetBranchesAsync`; supports optionally pinning either side to a specific commit via a second `GetCommitsAsync` call; saves/restores selections (including the last-used Branches/Pull Requests tab) per repo path via `ISettingsService.GetBranchState` / `SaveBranchState`; calls `GetDiffAsync` and stores result in `DiffSessionService.CurrentDiff`; navigates to `/diff`. Its Pull Requests tab instead lists PRs via `IPullRequestService` and, on selecting a card, resolves the compare ref via `GitService.FetchPullRequestRefAsync` (see **Pull request viewing** below)
3. `Components/Pages/MainView.razor` — reads `DiffSessionService.CurrentDiff`; hosts `DiffPanel` and `AnalysisPanel` in either a side-by-side or tabbed layout (both panels stay mounted in tab mode — hidden via CSS — to preserve state); Export Markdown writes to the user's Desktop
4. `Components/Pages/Settings.razor` — app settings page (theme, AI provider config, prompt overrides, etc.)

`DiffSessionService` is a singleton state bus — it exists because Blazor query params can't carry a full diff object.

**Panel components** (`Components/Panels/`):

- `DiffPanel.razor` — renders the file sidebar + collapsible diff tables; runs `SyntaxHighlighter` on each hunk at load time; receives `PendingFocus` from `MainView` to scroll/highlight a specific file/line
- `AnalysisPanel.razor` — shows AI results (explain + review) as cards, one per model run; lets users add, refresh, hide, and delete results; renders AI markdown via Markdig and post-processes it for analysis links (see below)

**Git operations** (`Services/GitService.cs`) run `git` as a subprocess directly via `System.Diagnostics.Process` (the `CliWrap` package reference in the `.csproj` is currently unused). `CreateNoWindow = true` is required on every invocation — git.exe is a console-subsystem binary, and launching it without that flag from a GUI process like MAUI pops a console window and breaks the inherited stdio handles. Each diff runs three subprocesses: `--numstat`, `--name-status`, and `--unified=N`, all using two-dot syntax (`base..compare`). The raw unified diff is parsed in-process into `DiffResult → DiffFile → DiffHunk → DiffLine`. A separate subprocess (`git log`) is used for commit pinning.

**Pull request viewing** — `Components/Pages/BranchSelection.razor`'s "Pull Requests" tab lists and compares PRs/MRs from whichever host the repo's `origin` remote points to, without ever needing `gh`/`glab`/etc. installed.

- `IPullRequestService` / `PullRequestService` detects the provider from the remote URL (GitHub, GitLab, Bitbucket Cloud, Azure DevOps are recognized automatically; anything else must be registered under Settings → Git Hosts as GitHub Enterprise, GitLab self-managed, Bitbucket Server/DC, or Gitea) and calls that provider's REST API directly to list PRs.
- Each provider has its own `I*AuthService` (`GitHubAuthService`, `GitLabAuthService`, `BitbucketAuthService`, `AzureDevOpsAuthService`). GitHub and GitLab use the standard OAuth Device Flow (no client secret required) against a baked-in public Client ID, overridable in Settings. Bitbucket Cloud and Azure DevOps instead use Authorization Code + PKCE with a local loopback `HttpListener` (`OAuthLoopback`) catching the browser redirect — Azure DevOps reuses the app's own Entra app registration (tenant `organizations`, so it only works for Microsoft work/school accounts, not personal MSAs); Bitbucket's OAuth client is confidential (always needs a secret), so its secret is never committed — it ships only in release builds, embedded from `Services/bitbucket-oauth-secrets.json` (gitignored locally, written by CI from `BITBUCKET_OAUTH_CLIENT_ID`/`_SECRET` repo secrets; see `bitbucket-oauth-secrets.example.json`). A pasted Personal Access Token / API token remains a fallback sign-in method for every provider. Self-hosted hosts registered under Settings → Git Hosts use a plain token via `IGitHostService`.
- `PullRequestInfo.SourceRemoteRef` is the ref to fetch for the compare side — a permanent synthetic ref on GitHub/GitLab/Bitbucket Server (`refs/pull/N/head`, `refs/merge-requests/N/head`, `refs/pull-requests/N/from`), or the live branch name/commit SHA on Bitbucket Cloud and Azure DevOps, which expose no such ref. `GitService.FetchPullRequestRefAsync` fetches it alongside the base branch and, if that fails (source branch deleted after merge), falls back to `PullRequestInfo.MergeCommitSha` — diffing the merge/squash commit against its own parent reproduces the same file changes without the original source commits. `PullRequestInfo.SourceAvailability` flags, at zero extra API cost, PRs where neither will be available (closed-without-merging on a host with no permanent ref).

**Syntax highlighting** (`Services/SyntaxHighlighter.cs`) wraps the ColorCode library using `HtmlClassFormatter` so that token colours are controlled entirely by CSS variables in `app.css` — giving free light/dark theme support. `GetLanguage` maps file extensions to `ILanguage` instances; several languages not built into ColorCode are implemented as inline `ILanguage` classes (Go, Rust, YAML, Bash, TOML, Dockerfile, T-SQL). C#, TypeScript, and JavaScript get two extra rules appended: method-call highlighting and PascalCase type highlighting. `HighlightLines` joins a hunk's lines, runs ColorCode over the whole block (to preserve cross-line token context), then splits the result back per-line while correctly closing/reopening any spans that straddle a newline.

> **Note:** `SyntaxHighlighter.GetLanguage` and `HighlightLines` currently write a debug log to `~/Desktop/hl-debug.txt`. This is a temporary debugging aid.

**AI integration** — DiffThis supports four AI providers, all sharing the same `PromptService`, `AiCacheService`, and `AnalysisLinkService` plumbing.

*Claude* (`Services/ClaudeService.cs`): invokes the `claude` CLI as a subprocess, passing the diff as stdin and using `--output-format text`. `ClaudeAuthService` reads credentials from `~/.claude/.credentials.json` (written by the Claude CLI after `claude auth login`, or by `TryInteractiveLoginAsync` launching that same command) and resolves the `claude` executable from well-known paths and `PATH`. OAuth token refresh is **not** delegated to the CLI — `ClaudeAuthService.RefreshAsync` POSTs directly to Anthropic's OAuth token endpoint (`https://platform.claude.com/v1/oauth/token`) with the stored refresh token and patches the new access token/expiry back into `.credentials.json` itself; the CLI subprocess is only used for interactive login and for reading the signed-in email (`claude auth status`). `ClaudeModelService` fetches available models from `GET /v1/models` using the auth token, sorts by tier (opus → sonnet → haiku) then version, persists to `Preferences`, and fires `ModelsChanged`.

*GitHub Copilot* (`DiffThis.AI.OpenAI/Services/`): `CopilotService` calls `https://api.githubcopilot.com/chat/completions` directly (OpenAI-compatible API). `CopilotAuthService` drives a device-code OAuth flow against GitHub (client ID `Iv1.b507a08c87ecfe98`, same as VS Code / copilot.vim), exchanges the OAuth token for short-lived Copilot session tokens via `api.github.com/copilot_internal/v2/token`, and stores credentials in SecureStorage. `CopilotModelService` fetches models from `https://api.githubcopilot.com/models`, filtering for chat-capable entries; falls back to a hardcoded list (gpt-4o, o1, o3-mini, claude-3.7-sonnet, gemini-2.0-flash, etc.) when the API is unavailable.

*OpenAI / ChatGPT* (`DiffThis.AI.OpenAI/Services/`): unlike the other three providers, `OpenAiService` delegates entirely to the official `codex` CLI (`npm install -g @openai/codex`) rather than calling an API directly — it pipes the prompt to `codex exec --sandbox read-only --ask-for-approval never --skip-git-repo-check --ephemeral -` over stdin and reads the response from stdout. `OpenAiAuthService` never reads or stores credentials itself; it shells out to `codex login status` / `codex login` / `codex logout` and requires ChatGPT browser sign-in (an API-key-authenticated CLI session is explicitly rejected, since DiffThis has no way to bill against it). `OpenAiModelService` fetches the model catalog via `codex debug models`, always keeping a CLI-default "Codex default" entry.

*Ollama* (`DiffThis.AI.OpenAI/Services/`): `OllamaService` calls the Ollama `/api/chat` endpoint with a dynamically calculated `num_ctx` (estimated from prompt length to avoid truncation). `OllamaEndpointService` manages a list of user-configured endpoints (name, base URL, optional API key, optional per-endpoint timeout, icon/badge customization), each with its own model list fetched from `/api/tags` (falling back to `/v1/models`). Endpoints and models are persisted via `ISettingsService.OllamaEndpointsJson`. The service also handles model pulls via the streaming `/api/pull` endpoint with live progress, cancellation, and recovery of in-progress pulls across app restarts. A `localhost`→`127.0.0.1` retry handles IPv6/IPv4 ambiguity on Windows.

Prompts are built by `PromptService`, which loads `review.md` / `explain.md` from embedded resources and renders `{{Variable}}` placeholders; users can override either template by placing a file at `%LOCALAPPDATA%\DiffThis\prompts\{name}.md`. The diff content is capped at 60,000 characters and truncated if longer. Available placeholders: `{{RepositoryName}}`, `{{BaseDisplay}}`, `{{CompareDisplay}}`, `{{FileCount}}`, `{{Additions}}`, `{{Deletions}}`, `{{FileList}}` (changed files with status + detected language), `{{DiffContent}}`. Results are cached per `(repoPath, baseRef, compareRef, feature, model, toolsEnabled, maxTurns, contextLines)` by `AiCacheService`, which persists to `%LOCALAPPDATA%\DiffThis\ai-cache.json` (max 500 entries, LRU-evicted to 400). The cache key type `AiRunKey` identifies a run configuration.

**Analysis links**: `AnalysisPanel` post-processes rendered Markdown to make file references (e.g. `Services/GitService.cs:42`) clickable. `AnalysisLinkService` parses AI output for file/line references using regex, resolves them to diff file indices, and builds a line-level index. Line references whose line number falls outside every hunk range in the file are treated as hallucinated and are excluded from the scroll index (but still show a file-level indicator). When a user clicks a reference, `AnalysisLinkService.RequestFocus` fires `FocusRequested`, which `MainView` catches and forwards to `DiffPanel` via `PendingFocus`. The link is dispatched from JS to .NET via a `DotNetObjectReference` registered as `setAnalysisRefDotNet`. References are classified by the `##` heading they appear under (Bug, LogicError, Security, Other) and carry a `RefSeverity` (Critical/High/Medium/Low/Unknown) detected from keywords in the surrounding text, falling back to a category-based default (Security/Bug → High, LogicError → Medium, Other → Low). Severity is shown in the indicator tooltip and used to modulate indicator opacity in the diff sidebar.

**Services** (all singletons, registered in `MauiProgram.cs`):

- `IGitService` / `GitService` — git subprocess wrapper + unified diff parser; `GetDiffAsync` accepts a `contextLines` parameter (default 3) passed as `--unified=N` to `git diff`; also fetches branches/commits and resolves PR compare refs (`FetchPullRequestRefAsync`, with the merge-commit fallback described above)
- `ISettingsService` / `SettingsService` — persists theme, font-ligatures toggle, recent-repo list, per-repo branch/PR-tab selection state, AI model/config preferences, OAuth client ID overrides, and `DiffContextLines` (3/10/25/50) via MAUI `Preferences` API; the Bitbucket OAuth client *secret* override is a genuine credential and is kept in `SecureStorage` instead, with an in-memory cache (`GetBitbucketOAuthClientSecretAsync`) since `Preferences`-backed settings are unencrypted
- `IExportService` / `ExportService` — generates Markdown from a `DiffResult` and writes it to a file
- `ILogService` / `LogService` — writes request/response/error entries per AI call to a daily log file under `%LOCALAPPDATA%\DiffThis\logs`, gated by `ISettingsService.AiLoggingEnabled`
- `IPullRequestService` / `PullRequestService` — detects the PR provider from the repo's remote and lists PRs/MRs via that provider's REST API
- `IGitHostService` / `GitHostService` — manages user-registered self-hosted git hosts (name, host, provider kind) and their tokens (SecureStorage), for providers with no well-known cloud host
- `IGitHubAuthService` / `GitHubAuthService`, `IGitLabAuthService` / `GitLabAuthService` — OAuth Device Flow sign-in for GitHub/GitLab, storing the token in SecureStorage
- `IBitbucketAuthService` / `BitbucketAuthService`, `IAzureDevOpsAuthService` / `AzureDevOpsAuthService` — browser-based OAuth (Authorization Code + PKCE via `OAuthLoopback`) for Bitbucket Cloud/Azure DevOps, falling back to a pasted token/PAT
- `IClaudeService` / `ClaudeService` — invokes `claude` CLI subprocess for diff review and explanation
- `IClaudeAuthService` / `ClaudeAuthService` — reads `~/.claude/.credentials.json`; resolves claude executable path; refreshes the OAuth token directly against Anthropic's token endpoint; exposes auth state and access token
- `IClaudeModelService` / `ClaudeModelService` — fetches available models from `GET /v1/models`; persists to `Preferences`; fires `ModelsChanged`
- `ICopilotService` / `CopilotService` — calls GitHub Copilot chat completions API for diff review/explanation
- `ICopilotAuthService` / `CopilotAuthService` — device-code OAuth for GitHub Copilot; stores credentials in SecureStorage; manages session token refresh
- `ICopilotModelService` / `CopilotModelService` — fetches and filters Copilot chat models; hardcoded fallback list
- `IOpenAiService` / `OpenAiService` — pipes the prompt to the `codex` CLI for diff review/explanation
- `IOpenAiAuthService` / `OpenAiAuthService` — shells out to `codex login`/`login status`/`logout`; requires ChatGPT browser sign-in, not an API key
- `IOpenAiModelService` / `OpenAiModelService` — fetches the model catalog via `codex debug models`
- `IOllamaService` / `OllamaService` — calls Ollama `/api/chat` for diff review/explanation; computes `num_ctx` dynamically
- `IOllamaEndpointService` / `OllamaEndpointService` — manages configured Ollama endpoints; fetches models, handles pulls with streaming progress, persists state via `SettingsService`
- `PromptService` — loads and renders prompt templates (embedded resources or user overrides)
- `IAnalysisLinkService` / `AnalysisLinkService` — parses AI markdown for file references, maps them to diff positions, fires `FocusRequested` events
- `AiCacheService` — persists AI responses keyed by diff + run config; no interface (injected directly)
- `DiffSessionService` — cross-page state (no interface; injected directly)
- `SyntaxHighlighter` — static class, no registration needed
- `OAuthLoopback` — static helper for native OAuth: PKCE pair/state generation, an `HttpListener`-based loopback redirect catcher, and browser launching; no registration needed

**JS interop**: `wwwroot/app.js` exposes `scrollToElement(id)` (used by `MainView` / `AnalysisPanel` to scroll panels), `copyToClipboard(text)`, and `setAnalysisRefDotNet` / `clearAnalysisRefDotNet` (registers the .NET callback for analysis link clicks).

**Model notes:**

- `DiffResult` carries both raw branch/commit refs (`BaseBranch`, `CompareBranch`) and human-readable display labels (`BaseLabel`, `CompareLabel`) — use `BaseDisplay` / `CompareDisplay` computed properties in the UI, as they fall back to the raw ref when no label is set.
- `BranchSelectionState` is persisted by `SettingsService` keyed on repo path, allowing branch + commit-pin selections, and the last-used Branches/Pull Requests tab (`UsePullRequests`), to survive restarts.
- `PullRequestInfo` carries `SourceRemoteRef` (what to fetch), `MergeCommitSha` (merge-commit fallback target), and `SourceAvailability` — see **Pull request viewing** above.
- `GitProviderKind` enumerates every supported provider (cloud and self-hosted); `GitHostConfig` is the user-registered record for a self-hosted one.

The `ViewModels/` and `Views/` directories exist on disk but are not wired up — Razor pages use inline `@code` blocks and inject services directly.
