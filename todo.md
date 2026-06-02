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

## Add Copilot support

## Add Ollama support

## Caching refactor

for caching context and code reviews, when comparing branches without a specified commit hash, seems to keep cached replies even when latest commit changes.