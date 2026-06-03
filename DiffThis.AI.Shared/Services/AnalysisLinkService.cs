using System.Text.RegularExpressions;
using DiffThis.Core.Models;
using DiffThis.AI.Shared.Models;

namespace DiffThis.AI.Shared.Services;

public partial class AnalysisLinkService : IAnalysisLinkService
{
    // ── Events ────────────────────────────────────────────────────────────
    public event Action?                   Changed        { add => _changed        += value; remove => _changed        -= value; }
    public event Action<int, int?, int?>?  FocusRequested { add => _focusRequested += value; remove => _focusRequested -= value; }
    private Action?                  _changed;
    private Action<int, int?, int?>? _focusRequested;

    // ── State ─────────────────────────────────────────────────────────────
    // Keyed by (fileIndex, newLineNumber) → refs touching that specific line.
    private Dictionary<(int, int), List<AnalysisRef>> _lineIndex = [];
    // Keyed by fileIndex → one rep ref per (RunKey, Category) for header indicators.
    private Dictionary<int, List<AnalysisRef>> _fileIndex = [];
    private readonly HashSet<AiRunKey> _hiddenKeys = [];

    // ── Regex ─────────────────────────────────────────────────────────────
    // Matches: filename.ext  OR  path/to/file.ext:42  OR  file.ext:42-54
    // Requires at least one dot in the filename portion (avoids plain words).
    [GeneratedRegex(
        @"(?<![/\\])([\w][\w.\-/]*\.[A-Za-z]{1,10})(?::(\d+)(?:-(\d+))?)?",
        RegexOptions.None)]
    private static partial Regex FileRefRegex();

    [GeneratedRegex(@"\b(critical|high|medium|low)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityKeywordRegex();

    // Matches a file reference with a known source-code extension inside <code> content.
    // Restricted to real extensions to avoid false positives like changedFields.add or e.Value.
    [GeneratedRegex(
        @"[\w][\w.\-/]*\.(?:ts|tsx|js|jsx|cs|py|go|rs|java|html|css|scss|sass|less|rb|kt|swift|vue|json|yaml|yml|toml|md|sql|sh|ps1|xml|php|cpp|hpp|h|c|fs|fsx|ex|exs|dart|r|lua|m|mm|tf|tfvars|env|prisma|proto|graphql|razor)(?::\d+(?:-\d+)?)?",
        RegexOptions.IgnoreCase)]
    public static partial Regex ExtractRefRegex();

    // ── Public API ────────────────────────────────────────────────────────

    public void Reset()
    {
        _lineIndex = [];
        _fileIndex = [];
        _hiddenKeys.Clear();
        _changed?.Invoke();
    }

    public void ResetIndexes()
    {
        _lineIndex = [];
        _fileIndex = [];
    }

    public void Refresh(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> entries)
    {
        var index = new Dictionary<(int fileIdx, int lineNum), List<AnalysisRef>>();

        foreach (var (runKey, entry) in entries)
        {
            foreach (var r in ParseRefs(runKey, entry.Response, diff))
            {
                if (r.LineFrom is null) continue; // file-only ref; needs a line to index
                var fileIdx = ResolveFileIndex(r.FilePath, diff);
                if (fileIdx < 0) continue;

                var from = r.LineFrom.Value;
                var to   = r.LineTo ?? from;
                for (var ln = from; ln <= to; ln++)
                {
                    // Skip individual lines that fall outside every hunk range.
                    // Checking per-line (not just LineFrom) prevents a range with a valid
                    // start but an out-of-hunk end from ballooning the index.
                    if (!LineIsInDiff(ln, diff.Files[fileIdx])) continue;

                    var key = (fileIdx, ln);
                    if (!index.TryGetValue(key, out var list))
                        index[key] = list = [];
                    // Deduplicate: one ref per (RunKey, lineFrom) per line
                    if (!list.Any(x => x.RunKey == r.RunKey && x.LineFrom == r.LineFrom))
                        list.Add(r);
                }
            }
        }

        _lineIndex = index;

        // Build file-level index: one entry per (fileIndex, RunKey, Category) so the
        // file header can show distinct icons without per-line matching.
        var fileIdx2 = new Dictionary<int, List<AnalysisRef>>();
        foreach (var ((fi, _), refs) in index)
        {
            if (!fileIdx2.TryGetValue(fi, out var fileList))
                fileIdx2[fi] = fileList = [];
            foreach (var r in refs)
            {
                if (!fileList.Any(x => x.RunKey == r.RunKey && x.Category == r.Category))
                    fileList.Add(r);
            }
        }
        // Also index file-only refs (LineFrom == null) that were skipped for _lineIndex
        foreach (var (runKey, entry) in entries)
        {
            foreach (var r in ParseRefs(runKey, entry.Response, diff))
            {
                var fi = ResolveFileIndex(r.FilePath, diff);
                if (fi < 0) continue;
                if (!fileIdx2.TryGetValue(fi, out var fileList))
                    fileIdx2[fi] = fileList = [];
                if (!fileList.Any(x => x.RunKey == r.RunKey && x.Category == r.Category))
                    fileList.Add(r);
            }
        }
        _fileIndex = fileIdx2;

        _changed?.Invoke();
    }

    public IReadOnlyList<AnalysisRef> GetFileRefs(int fileIndex)
    {
        return _fileIndex.TryGetValue(fileIndex, out var list)
            ? list.Where(r => !_hiddenKeys.Contains(r.RunKey)).ToList()
            : [];
    }

    public IReadOnlyList<AnalysisRef> GetLineRefs(int fileIndex, int newLineNumber)
    {
        return _lineIndex.TryGetValue((fileIndex, newLineNumber), out var list)
            ? list.Where(r => !_hiddenKeys.Contains(r.RunKey)).ToList()
            : [];
    }

    public bool IsVisible(AiRunKey key) => !_hiddenKeys.Contains(key);

    public void SetVisible(AiRunKey key, bool visible)
    {
        var changed = visible ? _hiddenKeys.Remove(key) : _hiddenKeys.Add(key);
        if (changed) _changed?.Invoke();
    }

    public void RequestFocus(string rawRef, DiffResult diff)
    {
        ParseRefText(rawRef, out var filePath, out var lineFrom, out var lineTo);
        var fileIdx = ResolveFileIndex(filePath, diff);
        _focusRequested?.Invoke(fileIdx, lineFrom, lineTo);
    }

    // ── Parsing ───────────────────────────────────────────────────────────

    private static List<AnalysisRef> ParseRefs(AiRunKey runKey, string markdown, DiffResult diff)
    {
        var refs    = new List<AnalysisRef>();
        var seen    = new HashSet<string>(); // deduplicate within a run

        // Split on ## headings to classify by section
        var sections = SplitSections(markdown);
        foreach (var (heading, body) in sections)
        {
            var category = ClassifyHeading(heading);
            foreach (Match m in FileRefRegex().Matches(body))
            {
                var rawPath = m.Groups[1].Value;
                // Filter out common false-positives (version strings, URLs, etc.)
                if (!LooksLikeFilePath(rawPath)) continue;

                int? lineFrom = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
                int? lineTo   = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : lineFrom;

                var dedupKey = $"{rawPath}:{lineFrom}-{lineTo}:{(int)category}:{runKey.Model}";
                if (!seen.Add(dedupKey)) continue;

                var severity = DetectSeverity(body, m.Index, category);
                refs.Add(new AnalysisRef(runKey, rawPath, lineFrom, lineTo, category, severity));
            }
        }
        return refs;
    }

    /// Split markdown into (heading, body) pairs.
    /// The first pair has an empty heading for content before the first ##.
    private static List<(string heading, string body)> SplitSections(string markdown)
    {
        var result  = new List<(string, string)>();
        var lines   = markdown.Split('\n');
        var heading = "";
        var bodyBuf = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("## \t", StringComparison.Ordinal))
            {
                result.Add((heading, bodyBuf.ToString()));
                heading = line[3..].Trim();
                bodyBuf.Clear();
            }
            else
            {
                bodyBuf.AppendLine(line);
            }
        }
        result.Add((heading, bodyBuf.ToString()));
        return result;
    }

    // Look for a severity keyword in the 200 chars *before* the ref (severity labels
    // almost always precede the file reference). Searching forward risks capturing
    // the label of the next finding instead.
    // Falls back to a category-based default if none found.
    private static RefSeverity DetectSeverity(string body, int refIndex, RefCategory category)
    {
        var start  = Math.Max(0, refIndex - 200);
        var window = body[start..refIndex];

        // Use the *last* match — it's closest to the ref and therefore most likely
        // to be the severity label for this specific finding rather than a prior one.
        Match? last = null;
        foreach (Match m in SeverityKeywordRegex().Matches(window))
            last = m;

        if (last is not null)
        {
            return last.Value.ToLowerInvariant() switch
            {
                "critical" => RefSeverity.Critical,
                "high"     => RefSeverity.High,
                "medium"   => RefSeverity.Medium,
                _          => RefSeverity.Low,   // only remaining match is "low"
            };
        }

        // Category-based fallback
        return category switch
        {
            RefCategory.Security   => RefSeverity.High,
            RefCategory.Bug        => RefSeverity.High,
            RefCategory.LogicError => RefSeverity.Medium,
            _                      => RefSeverity.Low,
        };
    }

    private static RefCategory ClassifyHeading(string heading)
    {
        var h = heading.ToLowerInvariant();
        if (h.Contains("bug"))      return RefCategory.Bug;
        if (h.Contains("logic"))    return RefCategory.LogicError;
        if (h.Contains("security")) return RefCategory.Security;
        return RefCategory.Other;
    }

    // Returns true if the line number falls within any hunk on either the old or new side.
    // Checking both sides avoids treating references to deleted lines as hallucinations.
    private static bool LineIsInDiff(int lineNum, DiffFile file)
    {
        foreach (var hunk in file.Hunks)
        {
            if (lineNum >= hunk.NewStart && lineNum < hunk.NewStart + hunk.NewCount)
                return true;
            if (lineNum >= hunk.OldStart && lineNum < hunk.OldStart + hunk.OldCount)
                return true;
        }
        return false;
    }

    private static bool LooksLikeFilePath(string s)
    {
        // Must have an extension segment that looks like a code file
        var lastDot = s.LastIndexOf('.');
        if (lastDot < 0) return false;
        var ext = s[(lastDot + 1)..].ToLowerInvariant();
        // Allow common source extensions; reject numeric-only (version numbers like 1.2.3)
        if (!ext.Any(char.IsLetter)) return false;
        // Reject purely numeric segments before the dot (e.g. "3.14")
        var namePart = s[..lastDot];
        if (namePart.All(c => char.IsDigit(c) || c == '.')) return false;
        return true;
    }

    private static int ResolveFileIndex(string rawPath, DiffResult diff)
    {
        for (var i = 0; i < diff.Files.Count; i++)
        {
            var f = diff.Files[i];
            if (Matches(rawPath, f.DisplayPath) ||
                Matches(rawPath, f.NewPath)     ||
                Matches(rawPath, f.OldPath))
                return i;
        }
        return -1;
    }

    private static bool Matches(string rawPath, string filePath) =>
        filePath.EndsWith(rawPath,  StringComparison.OrdinalIgnoreCase) ||
        rawPath.EndsWith(filePath,  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(Path.GetFileName(rawPath), StringComparison.OrdinalIgnoreCase);

    /// Parse a raw ref string like "deal.ts:42-54" into components.
    internal static void ParseRefText(string raw, out string filePath, out int? lineFrom, out int? lineTo)
    {
        var m = FileRefRegex().Match(raw);
        if (m.Success)
        {
            filePath = m.Groups[1].Value;
            lineFrom = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
            lineTo   = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : lineFrom;
        }
        else
        {
            filePath = raw;
            lineFrom = null;
            lineTo   = null;
        }
    }
}
