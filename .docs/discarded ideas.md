# Discarded Ideas — LLM Code Review Process

These steps from the [LLM-Based Automated Code Review Process](<LLM-Based%20Automated%20Code%20Review%20Process.md>) and [How to Determine if Full File Context is Required](<How%20to%20Determine%20if%20Full%20File%20Context%20is%20required.md>) docs were considered but are not practical for DiffThis.

---

## Trigger Event / PR Webhooks

The process doc describes kicking off a review pipeline when a PR is created or updated via a webhook.

**Why discarded:** DiffThis is a manually-operated desktop app. There is no server component to receive inbound webhook calls, and no persistent daemon running between user sessions. Users trigger reviews explicitly by selecting branches and clicking the review button.

---

## Scheduled Batch Review

Running reviews on a schedule (e.g. nightly over all open PRs).

**Why discarded:** Same reason as above — DiffThis has no background service. Scheduled execution would require a separate process or system task scheduler, which is outside the app's scope.

---

## CI/CD Pipeline Integration

Embedding DiffThis as a step in a GitHub Actions / Azure DevOps pipeline that posts review results as pipeline artefacts or PR checks.

**Why discarded:** DiffThis has no CLI mode and produces no structured output files compatible with pipeline step contracts. A separate tool or GitHub Action built around the same LLM integration idea would be the right vehicle for this (see `todo.md` — GitHub/GitLab PR comment posting).

---

## Fine-Tuning From Developer Feedback

Collecting accepted/ignored suggestions to fine-tune the model over time.

**Why discarded:** Fine-tuning requires: a labelled training dataset, model API access beyond what Claude CLI / Copilot chat expose, training infrastructure, and ongoing maintenance. This is far outside the scope of a local desktop tool. Prompt tuning from feedback is a more feasible future direction (see `todo.md`).

---

## Confidence Scoring

Filtering or ranking AI findings by the model's confidence in each finding.

**Why discarded:** Neither the Claude CLI (`--output-format text`) nor the GitHub Copilot chat completions API exposes token-level log-probabilities, logits, or structured confidence scores. There is no reliable way to obtain model confidence without API-level access that neither provider currently exposes to DiffThis.

---

## AST-Based Symbol Resolution

Using per-language AST parsers to detect undefined symbols in a diff and automatically trigger full-file context expansion.

**Why discarded:** This would require maintaining parser implementations or external tool integrations for every language DiffThis supports (C#, TypeScript, Python, Go, Rust, Java, …). The maintenance burden is disproportionate to the benefit, especially since increasing context lines (the `DiffContextLines` setting) already covers the majority of cases where missing symbol definitions cause reviewer confusion.

---

## Masking Sensitive Data Before LLM Calls

Scrubbing secrets and PII from the diff before it is sent to the AI provider.

**Why discarded:** DiffThis reviews local diffs on the user's own machine, using their own AI credentials. The user is in control of what is in their diff. A regex-based scrubber would produce false positives on legitimate variable names (e.g. `apiKey = getApiKey()`, `token := cfg.Token`) and would give users a false sense of security while making review results less useful.

---

## Developer "Responds to Comments" Threading

A workflow where the developer writes responses to individual AI findings, and the system re-reviews only the affected lines.

**Why discarded:** DiffThis has no comment threading UI. The intended workflow is that the developer applies fixes, re-runs the diff, and runs a fresh review — which is already fully supported. Adding in-app comment threads would be a significant UI scope expansion (see `todo.md` — developer feedback loop).
