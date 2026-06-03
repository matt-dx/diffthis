# ✅ How to Determine if Full File Context is Required

You can treat this as a **decision layer with heuristics + signals**. Below are practical criteria you can implement.

***

# 🧠 1. Start with Diff-Only by Default

Always begin with:

* ✅ Code diff
* ✅ Small surrounding context (e.g., ±20 lines)

Then escalate only if needed.

***

# 🔍 2. Heuristics That Signal “Full Context Needed”

## A. **Change Type Heuristics**

### ✅ Require Full Context if:

* Function signature changed
* Class/interface modified
* Public API altered
* Control flow modified (loops, conditionals)

**Why:** Behavior depends on other parts of the file.

***

## B. **Dependency Signals**

### ✅ Trigger when diff references:

* Variables not defined within diff
* Functions defined elsewhere in same file
* Class state or shared mutable state

**Example:**

```python
def process():
    return calculate_total(x)
```

If `calculate_total` isn’t in the diff → you need full file.

***

## C. **Cross-Line Logical Coupling**

### ✅ Trigger when:

* Validation logic is partial
* Try/catch blocks are incomplete
* Initialization happens elsewhere

**Example:**

```js
if (user.isAdmin) {
   grantAccess();
}
```

You need context to know:

* How `user` is created
* Whether `isAdmin` is reliable

***

## D. **Stateful or Side-Effect Code**

### ✅ Require full file when:

* Global variables are used
* Object state is mutated
* Singleton/service patterns exist

***

## E. **Security-Sensitive Patterns**

Always expand context when detecting:

* Authentication / authorization logic
* Database queries
* File I/O
* External API calls

**Why:** Vulnerabilities often depend on surrounding code.

***

## F. **Large or Fragmented Diffs**

### ✅ Expand when:

* Diff spans multiple non-contiguous chunks
* > N lines changed (e.g., 100+)
* Multiple functions partially modified

***

## G. **Low Confidence from LLM (Adaptive Loop)**

Run diff-only first, then:

### If LLM shows:

* Uncertainty (“cannot determine…”, “missing context…”)
* Generic feedback
* Contradictions

➡️ Retry with full file

***

# ⚙️ 3. Implementing a Decision Engine

You can formalize this as a scoring system:

```pseudo
context_score = 0

if function_signature_changed:
    context_score += 3

if undefined_symbols_present:
    context_score += 3

if security_sensitive_code:
    context_score += 4

if diff_size > threshold:
    context_score += 2

if stateful_patterns_detected:
    context_score += 2

if LLM_low_confidence:
    context_score += 5

if context_score >= threshold:
    include_full_file = True
```

***

# 🧩 4. Hybrid Approach (Best Practice)

Instead of “all-or-nothing”, use **progressive expansion**:

### Level 1: Minimal

* Diff + ±20 lines

### Level 2: Local Context

* Entire function / class

### Level 3: Full File

* Entire file

### Level 4 (rare):

* Related files (imports, dependencies)

***

# 🧪 5. Practical Detection Techniques

## A. Static Parsing (Best option)

Use AST tools:

* Detect function boundaries
* Identify symbol definitions
* Track references

***

## B. Regex/Heuristic Signals (Simple fallback)

Look for:

* `def`, `class`, `function`
* `import` changes
* `this.` / `self.` usage

***

## C. Symbol Resolution

Check:

* Is every identifier in diff resolvable locally?
  * ❌ No → expand context

***

# 📊 6. Example Decision Scenarios

## Example 1: Simple Change

```js
return total + tax;
```

✅ Diff-only is enough

***

## Example 2: Function Call Added

```js
return calculateDiscount(price);
```

⚠️ Need:

* Function definition → expand to function or file

***

## Example 3: Auth Logic Change

```python
if user.role == "admin":
```

🚨 Full file required (security-sensitive)

***

## Example 4: Partial Class Change

```java
this.balance += amount;
```

⚠️ Need:

* Class fields
* Constructor logic

***

# 🚀 7. Advanced Strategy: Two-Pass LLM

You can let the LLM help decide:

### Pass 1:

Ask:

> “Is the provided diff sufficient for a reliable review?”

### If “no”:

→ automatically fetch more context

***

# ✅ Key Takeaways

* **Default to diff**, expand only when needed
* Use:
  * Structural signals (functions/classes)
  * Semantic signals (dependencies/state)
  * Risk signals (security-sensitive code)
* Implement **progressive context expansion**
* Add a **fallback loop based on LLM confidence**

***

If you want, I can give you:

* Exact **code for a context decision engine**
* A **GitHub Action implementation**
* Or a **prompt that asks the LLM to self-diagnose missing context**
