# ✅ LLM-Based Automated Code Review – Process Flow

## 1. **Trigger Event**

* Developer action triggers the pipeline:
  * Pull Request (PR) created/updated
  * Commit pushed to branch
  * Scheduled batch review (optional)

**Output:** Code diff / changed files

***

## 2. **Pre-Processing & Context Gathering**

Prepare structured input for the LLM.

### Steps:

* Extract:
  * Code diffs (preferred over full files)
  * Full file context (if needed for understanding)
* Collect metadata:
  * Programming language
  * Repository rules / lint configs
  * Coding standards
  * Security policies
* Optional enrichment:
  * Recent commit history
  * Related files / dependencies

**Output:** Structured prompt payload

***

## 3. **Static Analysis (Optional but Recommended)**

Run traditional tools before LLM:

* Linters (e.g., ESLint, Pylint)
* Formatters (Prettier, Black)
* Security scanners (SAST tools)

**Purpose:**

* Catch deterministic issues early
* Reduce load on LLM
* Provide signals as input to LLM

**Output:**

* Static analysis findings (passed to LLM)

***

## 4. **Prompt Engineering Layer**

Construct a high-quality LLM prompt.

### Components:

* System instructions:
  * “You are a senior software reviewer…”
* Constraints:
  * Focus areas: readability, performance, security, best practices
* Input:
  * Code diff
  * Static analysis outputs
  * Context files (if needed)

### Example prompt structure:

```text
Review the following code changes.

Focus on:
- Bugs or logical errors
- Security vulnerabilities
- Performance issues
- Code readability and maintainability

Provide:
- Inline comments
- Severity (low/medium/high)
- Suggested fixes

Code:
<diff here>
```

***

## 5. **LLM Review Execution**

Send prompt to LLM API.

### LLM tasks:

* Analyze code semantics
* Identify issues
* Suggest improvements
* Detect anti-patterns

**Output:**

* Structured or semi-structured review:
  * Comments
  * Severity levels
  * Suggested fixes

***

## 6. **Post-Processing & Normalization**

Convert raw LLM output into actionable format.

### Steps:

* Parse output into structured JSON:
  * File
  * Line reference
  * Issue type
  * Recommendation
* Deduplicate comments
* Filter noise (optional thresholds)

**Example JSON:**

```json
{
  "file": "auth.py",
  "line": 42,
  "severity": "high",
  "issue": "Potential SQL injection",
  "suggestion": "Use parameterized queries"
}
```

***

## 7. **Validation & Guardrails**

Ensure reliability before exposing results.

### Checks:

* Confidence scoring (if supported)
* Rule-based validation:
  * Ignore hallucinated files/lines
* Compare with static analysis results

***

## 8. **Feedback Delivery**

Publish results back to developer workflow.

### Options:

* PR comments (GitHub / GitLab / Azure DevOps)
* Inline annotations
* Review summary

### Example:

* Inline comment on changed line
* Summary:
  * ✅ Passed checks
  * ⚠️ 3 warnings
  * ❌ 1 critical issue

***

## 9. **Developer Interaction Loop**

Allow iterative improvement.

* Developer:
  * Applies fixes
  * Responds to comments
* System:
  * Re-runs review automatically

***

## 10. **Learning & Continuous Improvement**

(Optional but powerful)

### Collect:

* Developer feedback:
  * Accepted suggestions
  * Ignored comments

### Use for:

* Prompt tuning
* Fine-tuning model
* Rule adjustments

***

# 🔁 Simplified Flowchart (Text Version)

```text
[PR Created / Code Commit]
            ↓
[Extract Code Diff + Context]
            ↓
[Run Static Analysis Tools]
            ↓
[Build LLM Prompt]
            ↓
[Call LLM API]
            ↓
[Parse & Structure Output]
            ↓
[Validate & Filter Results]
            ↓
[Post Comments to PR]
            ↓
[Developer Fixes Code]
            ↓
[Re-trigger Pipeline]
```

***

# 🧠 Key Design Considerations

### 1. **Diff-Based vs Full File**

* Prefer diffs to reduce cost and noise
* Use full context when logic spans multiple files

### 2. **Token Optimization**

* Chunk large PRs
* Prioritize critical files

### 3. **Hybrid Approach**

Combine:

* Deterministic tools → accuracy
* LLM → reasoning

### 4. **Latency vs Quality Tradeoff**

* Parallelize file reviews
* Use smaller models for quick checks

### 5. **Security & Privacy**

* Avoid sending secrets
* Mask sensitive data before LLM call

***

# ✅ Optional Enhancements

* Auto-fix generation (apply patches)
* Risk scoring per PR
* Domain-specific reviewers (security, performance)
* Multi-pass review (quick scan → deep review)

***

If you want, I can turn this into a **diagram image**, **architecture diagram**, or even a **working reference implementation (GitHub Action / Azure DevOps pipeline)**.
