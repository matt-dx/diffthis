# DiffThis — Planned Features

## User-defined prompt overrides

Allow users to customise the AI prompts for Review and Explain on a per-model basis.

**How it should work:**

- `PromptService.LoadTemplate` already checks `%LOCALAPPDATA%\DiffThis\prompts\{name}.md` before
  falling back to the embedded default. That path acts as the global override slot.
- Extend this to support per-model overrides, e.g.:
  - `%LOCALAPPDATA%\DiffThis\prompts\{model}\{name}.md`  (model-specific, e.g. `claude-opus-4-8\review.md`)
  - `%LOCALAPPDATA%\DiffThis\prompts\{name}.md`           (global override, any model)
  - Embedded default (shipped with the app)
- Expose a "Prompts" section in Settings that shows the active template for each feature,
  lets the user open the file in their editor, and shows which resolution tier is active
  (user-model / user-global / built-in).
- The `{{Variable}}` placeholder contract is public API — document it alongside the Settings UI.

**Variables available in templates:**

| Placeholder         | Value                                      |
|---------------------|--------------------------------------------|
| `{{RepositoryName}}`| Repo folder name                           |
| `{{BaseDisplay}}`   | Human-readable label for the base ref      |
| `{{CompareDisplay}}`| Human-readable label for the compare ref   |
| `{{FileCount}}`     | Number of files changed                    |
| `{{Additions}}`     | Total lines added                          |
| `{{Deletions}}`     | Total lines deleted                        |
| `{{DiffContent}}`   | Full unified diff (truncated at 60 k chars)|

## Allow open Repository from URI

Allow opening and comparing a repo without storing it locally

## Add Ollama support

## Caching refactor

for caching context and code reviews, when comparing branches without a specified commit hash, seems to keep cached replies even when latest commit changes.

---

## Static analysis pre-pass

Run language-appropriate linters/scanners (ESLint, Pylint, dotnet-analyzers, etc.) against the changed files before calling the LLM, and include their findings as additional context in the prompt. Directly implements step 3 of the LLM review process doc.

**How it might work:**
- Detect changed file languages from extensions
- Invoke the appropriate tool per language if available on PATH
- Pass structured findings (file, line, rule, message) into a `{{StaticAnalysis}}` prompt placeholder
- Cache tool output alongside AI results so it is not re-run unnecessarily

**Why deferred:** Requires detecting and invoking per-language tools, handling tool unavailability gracefully, and keeping the finding format normalised across many different linters.

---

## Two-pass review (context sufficiency check)

First LLM pass asks: "Is the provided diff sufficient for a reliable review?" If the answer is no, automatically fetch more context (larger `--unified=N` or full file) and run a second pass.

**How it might work:**
- Send a short system prompt + diff asking the LLM to respond with a JSON verdict: `{ "sufficient": bool, "reason": string }`
- If `sufficient == false`, re-run `GetDiffAsync` with a larger context window or fetch full file content
- Retry the full review with the expanded context

**Why deferred:** Requires a structured response protocol, an extra LLM call per review, and file-fetch infrastructure (`git show HEAD:<path>`).

---

## Full file context inclusion

When heuristics detect that a diff references symbols not defined within the diff (changed function signatures, cross-file dependencies, security-sensitive patterns), fetch the full file content from git and include it as supplementary context.

**Heuristics to trigger (from `.docs/How to Determine if Full File Context is required.md`):**
- Function signature changed
- Security-sensitive patterns detected (auth, DB queries, file I/O, external API calls)
- Diff spans many non-contiguous hunks (large/fragmented diff)
- Diff size exceeds threshold (e.g. 100+ lines changed)

**How it might work:**
- `git show HEAD:<path>` subprocess per candidate file
- Token budget check — only include if remaining budget allows
- Pass as a separate `{{FileContext}}` block after the diff

**Why deferred:** Requires per-file subprocess calls, token budget management, and a decision engine to avoid inflating context unnecessarily.

---

## GitHub / GitLab PR comment posting

Post AI review results directly as inline review comments on a pull request.

**How it might work:**
- Map `AnalysisRef` line numbers back to the diff position format required by the GitHub PR Review API
- Use the repo's `RemoteUri` to determine provider (GitHub vs GitLab)
- OAuth scope: `repo` (GitHub) or `api` (GitLab)
- UI: "Post to PR" button in AnalysisPanel, with confirmation modal showing comment count

**Why deferred:** Requires additional OAuth scopes beyond what is used today, GitHub/GitLab API integration, diff-position mapping (GitHub uses a non-obvious `position` field in patch hunks), and handling rate limits and partial failures.

---

## Auto-fix generation

Generate and optionally apply suggested code patches from AI findings.

**How it might work:**
- Request patches in unified diff format from the LLM alongside review findings
- Parse patch blocks from the AI response
- Present a "Apply fix" button per finding, writing the patch to disk via `git apply`

**Why deferred:** Requires reliable LLM patch output (currently inconsistent), diff-patch parsing, file write-back, and conflict detection.

---

## Risk scoring per PR

Show an overall risk badge (low / medium / high / critical) on the analysis panel derived from the severity distribution across all findings in a review run.

**How it might work:**
- After `AnalysisLinkService.Refresh()`, compute: max severity found, or weighted sum
- Display as a coloured badge in the AnalysisPanel card header
- Include in Markdown export

**Why deferred:** Relatively small lift — held back only because the severity detection itself (`RefSeverity`) was just added and needs real-world validation before basing a summary score on it.

---

## Domain-specific review profiles

Allow the user to select a review mode when starting a run: General, Security audit, Performance review, etc. Each mode uses a different prompt template tuned for that focus area.

**How it might work:**
- Add a `ReviewProfile` enum: `General | Security | Performance | Accessibility`
- Ship separate embedded prompt templates per profile (`review-security.md`, `review-performance.md`, etc.)
- Add a profile selector to the "Add analysis" dropdown in AnalysisPanel
- Include profile in the `AiRunKey` cache key

**Why deferred:** Requires writing and maintaining multiple high-quality prompt templates and exposing the profile selector in the UI without making it noisy.

---

## Multi-pass review (quick scan → deep review)

Run a fast, cheap model over all changed files first to triage which files need deep attention, then run a thorough review only on the flagged files with a more capable model.

**How it might work:**
- Pass 1: cheap model (e.g. haiku / gpt-4o-mini), single-message, returns a JSON list of file risk scores
- Pass 2: expensive model, receives only the high-risk files with extended context
- Show both passes in AnalysisPanel as separate result cards

**Why deferred:** Requires orchestrating two dependent LLM calls, structured output from pass 1, and a UI representation for the multi-pass result chain.

---

## Developer feedback loop

Allow users to mark individual AI findings as helpful or not helpful, and use that signal to tune prompts over time.

**How it might work:**
- Thumbs up / thumbs down per `AnalysisRef` in the context menu or AnalysisPanel
- Store feedback in a local `feedback.json` alongside `ai-cache.json`
- Aggregate feedback to surface which prompt phrasing produces the most accepted findings
- Eventually: inject a `{{FeedbackExamples}}` block into the prompt with accepted/rejected examples

**Why deferred:** Requires feedback storage, aggregation logic, and a feedback-to-prompt pipeline. Value is high but the feedback collection surface needs careful UX design to avoid being intrusive.