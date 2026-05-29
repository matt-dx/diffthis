using System.Collections.Generic;
using System.IO;
using System.Text;
using ColorCode;
using ColorCode.Common;

namespace DiffThis.Services;

/// <summary>
/// Wraps ColorCode.HTML to provide per-hunk syntax highlighting for the diff view.
/// Uses <see cref="HtmlClassFormatter"/> so colours are controlled by CSS classes in
/// app.css, which reference CSS variables — giving free light/dark theme support.
/// </summary>
public static class SyntaxHighlighter
{
    private static readonly HtmlClassFormatter _formatter = new();

    // -------------------------------------------------------------------------
    // Language lookup
    // -------------------------------------------------------------------------

    // Enhanced versions of built-in languages with method-name and PascalCase-type rules.
    // Lazy so we only pay the rule-list copy cost when the language is first needed.
    private static readonly Lazy<ILanguage> _csharpEnhanced    = new(() => Enhance(Languages.CSharp));
    private static readonly Lazy<ILanguage> _tsEnhanced        = new(() => Enhance(Languages.Typescript));
    private static readonly Lazy<ILanguage> _jsEnhanced        = new(() => Enhance(Languages.JavaScript));

    /// <summary>
    /// Maps a file name to an <see cref="ILanguage"/> for highlighting, or returns
    /// <see langword="null"/> for unsupported extensions (caller renders plain text).
    /// </summary>
    public static ILanguage? GetLanguage(string fileName) =>
        Path.GetExtension(fileName).TrimStart('.').Trim().ToLowerInvariant() switch
        {
            "cs"                                               => _csharpEnhanced.Value,
            "ts" or "tsx"                                      => _tsEnhanced.Value,
            "js" or "jsx" or "mjs" or "cjs"                    => _jsEnhanced.Value,
            "json" or "jsonc"                                  => Languages.FindById("json"),
            "css" or "scss" or "sass" or "less"                => Languages.Css    ?? Languages.FindById("css"),
            "html" or "htm"                                    => Languages.Html   ?? Languages.FindById("html"),
            "xml" or "xaml" or "csproj" or "props"
                or "targets" or "razor" or "cshtml"            => Languages.Xml    ?? Languages.FindById("xml"),
            "py" or "pyw"                                      => Languages.Python ?? Languages.FindById("python"),
            // Languages.Sql resolves to null inside the MAUI runtime — use our custom T-SQL class
            "sql"                                              => TSqlLang.Instance,
            "md" or "markdown"                                 => Languages.Markdown   ?? Languages.FindById("markdown"),
            "ps1" or "psm1" or "psd1"                          => Languages.PowerShell ?? Languages.FindById("powershell"),
            "go"                                               => GoLang.Instance,
            "rs"                                               => RustLang.Instance,
            "yaml" or "yml"                                    => YamlLang.Instance,
            "sh" or "bash" or "zsh"                            => BashLang.Instance,
            "toml"                                             => TomlLang.Instance,
            "dockerfile"                                       => DockerfileLang.Instance,
            _                                                  => null
        };

    /// <summary>
    /// Appends two extra rules to an existing language's rule list:
    /// <list type="bullet">
    ///   <item>Method/function call names — identifier immediately before <c>(</c>
    ///         → <c>builtinFunction</c> CSS class (gold/yellow).</item>
    ///   <item>PascalCase type names — any <c>[A-Z][A-Za-z0-9_]+</c> identifier not
    ///         already consumed by an earlier rule → <c>type</c> CSS class (teal).</item>
    /// </list>
    /// Both rules are appended last so existing keyword/string/comment rules win at
    /// any position where they also match.
    /// </summary>
    private static ILanguage Enhance(ILanguage source)
    {
        var rules = source.Rules.ToList();

        // Method / function call names: identifier directly before '('
        // Appended last — existing keyword rules (e.g. typeof, nameof) will have
        // already been matched at positions they share with this rule.
        rules.Add(new LanguageRule(
            @"\b[A-Za-z_]\w*(?=\s*\()",
            new Dictionary<int, string> { [0] = ScopeName.BuiltinFunction }));

        // PascalCase identifiers: types, classes, interfaces, enums, etc.
        // Requires ≥ 2 chars so lone uppercase letters (like 'I' on its own) are skipped.
        rules.Add(new LanguageRule(
            @"\b[A-Z][A-Za-z0-9_]+\b",
            new Dictionary<int, string> { [0] = ScopeName.Type }));

        return new LanguageWrapper(source, rules);
    }

    // Thin wrapper that delegates everything to a source ILanguage but uses a
    // different (augmented) rules list.
    private sealed class LanguageWrapper(ILanguage src, IList<LanguageRule> rules) : ILanguage
    {
        public string  Id               => src.Id;
        public string  Name             => src.Name;
        public string  CssClassName     => src.CssClassName;
        public string? FirstLinePattern => src.FirstLinePattern;
        public bool    HasAlias(string alias) => src.HasAlias(alias);
        public IList<LanguageRule> Rules => rules;
    }

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Highlights <paramref name="lines"/> as a contiguous code block (preserving
    /// cross-line token context within the hunk), then splits the result back into
    /// one HTML string per line.  Falls back to HTML-escaped plain text on any error.
    /// </summary>
    public static List<string> HighlightLines(IReadOnlyList<string> lines, ILanguage language)
    {
        try
        {
            var code    = string.Join("\n", lines);
            var rawHtml = _formatter.GetHtmlString(code, language);
            var inner   = StripWrapper(rawHtml);
            return SplitHighlightedLines(inner);
        }
        catch
        {
            return [.. lines.Select(EscapeHtml)];
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Strips the <c>&lt;div class="lang"&gt;&lt;pre&gt;</c> … <c>&lt;/pre&gt;&lt;/div&gt;</c>
    /// wrapper that ColorCode adds around its output.
    /// </summary>
    private static string StripWrapper(string html)
    {
        var preIdx = html.IndexOf("<pre>", StringComparison.OrdinalIgnoreCase);
        if (preIdx < 0) return html;
        var start = preIdx + 5; // skip "<pre>"
        if (start < html.Length && html[start] == '\r') start++; // skip \r in Windows \r\n
        if (start < html.Length && html[start] == '\n') start++;
        var end = html.LastIndexOf("</pre>", StringComparison.OrdinalIgnoreCase);
        if (end < 0 || end <= start) return html;
        return html[start..end];
    }

    /// <summary>
    /// Splits highlighted HTML (which contains <c>&lt;span&gt;</c> tokens) by newlines
    /// while correctly closing and re-opening any spans that straddle a line boundary,
    /// ensuring each element is a complete, valid HTML fragment.
    /// </summary>
    private static List<string> SplitHighlightedLines(string html)
    {
        var result   = new List<string>();
        var openTags = new List<string>(); // oldest-first; acts as a stack
        var current  = new StringBuilder();
        int i = 0;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                int end = html.IndexOf('>', i);
                if (end < 0) { current.Append(html[i..]); break; }
                var tag = html[i..(end + 1)];

                if (tag.StartsWith("</"))
                {
                    if (openTags.Count > 0) openTags.RemoveAt(openTags.Count - 1);
                }
                else if (!tag.EndsWith("/>"))
                {
                    openTags.Add(tag);
                }

                current.Append(tag);
                i = end + 1;
            }
            else if (html[i] == '\n')
            {
                // Close open spans at end of line…
                for (int j = openTags.Count - 1; j >= 0; j--)
                    current.Append("</span>");
                result.Add(current.ToString());
                current.Clear();
                // …and reopen them at start of next line.
                foreach (var t in openTags) current.Append(t);
                i++;
            }
            else
            {
                current.Append(html[i++]);
            }
        }

        // Add the last segment only if non-empty (suppresses the phantom empty line
        // that appears when ColorCode appends a trailing \n before </pre>).
        var last = current.ToString();
        if (last.Length > 0 || result.Count == 0)
            result.Add(last);

        return result;
    }

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // =========================================================================
    // Custom language definitions
    // =========================================================================

    private sealed class GoLang : ILanguage
    {
        public static readonly GoLang Instance = new();

        public string  Id               => "go";
        public string  Name             => "Go";
        public string  CssClassName     => "go";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            // Block comments before line comments so /* is not swallowed by //
            new(@"/\*[\s\S]*?\*/",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            new(@"//[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            // Raw string literals (backtick) before interpreted strings
            new(@"`[^`]*`",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            new(@"""(?:[^""\\]|\\.)*""",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            new(@"'(?:[^'\\]|\\.)'",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            new(@"\b(break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            new(@"\b(bool|byte|complex64|complex128|error|float32|float64|int|int8|int16|int32|int64|rune|string|uint|uint8|uint16|uint32|uint64|uintptr)\b",
                new Dictionary<int, string> { [0] = ScopeName.Type }),
            new(@"\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b",
                new Dictionary<int, string> { [0] = ScopeName.Number }),
        ];
    }

    private sealed class RustLang : ILanguage
    {
        public static readonly RustLang Instance = new();

        public string  Id               => "rust";
        public string  Name             => "Rust";
        public string  CssClassName     => "rust";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            new(@"/\*[\s\S]*?\*/",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            new(@"//[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            // Attributes: #[...] and #![...]
            new(@"#!?\[[^\]]*\]",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            // Lifetime parameters: 'a
            new(@"'[a-z_][a-z_0-9]*\b",
                new Dictionary<int, string> { [0] = ScopeName.Type }),
            // Char literals (after lifetime so 'a: is not a char)
            new(@"'(?:[^'\\]|\\.)+'",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Raw string literals: r#"..."#
            new(@"r#*""[\s\S]*?""#*",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Byte strings: b"..."
            new(@"b""(?:[^""\\]|\\.)*""",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            new(@"""(?:[^""\\]|\\.)*""",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            new(@"\b(as|break|const|continue|crate|dyn|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|Self|self|static|struct|super|trait|true|type|unsafe|use|where|while|async|await)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            new(@"\b(bool|char|f32|f64|i8|i16|i32|i64|i128|isize|str|u8|u16|u32|u64|u128|usize|String|Vec|Option|Result|Box|Rc|Arc|HashMap|HashSet)\b",
                new Dictionary<int, string> { [0] = ScopeName.Type }),
            new(@"\b0x[0-9a-fA-F_]+\b|\b0b[01_]+\b|\b0o[0-7_]+\b|\b\d[\d_]*(?:\.[\d_]+)?(?:[eE][+-]?[\d_]+)?\b",
                new Dictionary<int, string> { [0] = ScopeName.Number }),
        ];
    }

    private sealed class YamlLang : ILanguage
    {
        public static readonly YamlLang Instance = new();

        public string  Id               => "yaml";
        public string  Name             => "YAML";
        public string  CssClassName     => "yaml";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            new(@"#[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            // Document separators
            new(@"(?m)^---\s*$|(?m)^\.\.\.\s*$",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            // Double-quoted strings
            new(@"""(?:[^""\\]|\\.)*""",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Single-quoted strings
            new(@"'[^']*'",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Keys: word chars at start of value (after optional leading spaces/dashes)
            new(@"(?m)^\s*[\w\-\.]+(?=\s*:)",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            // Anchors & aliases
            new(@"[&*][a-zA-Z_][\w\-]*",
                new Dictionary<int, string> { [0] = ScopeName.Type }),
            // Booleans and null
            new(@"\b(true|false|null|~|yes|no|on|off)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            new(@"\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b",
                new Dictionary<int, string> { [0] = ScopeName.Number }),
        ];
    }

    private sealed class BashLang : ILanguage
    {
        public static readonly BashLang Instance = new();

        public string  Id               => "bash";
        public string  Name             => "Bash";
        public string  CssClassName     => "bash";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            new(@"#[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            new(@"""(?:[^""\\]|\\.)*""|'[^']*'",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // $VAR, ${VAR}, $(...), $((...))
            new(@"\$\(\([^)]*\)\)|\$\([^)]*\)|\$\{[^}]*\}|\$[a-zA-Z_@#?!*\-0-9][a-zA-Z_0-9]*",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            new(@"\b(if|then|else|elif|fi|for|while|until|do|done|case|esac|in|function|return|break|continue|exit|export|local|readonly|declare|typeset|source|alias|unset|set|shift|trap|eval|exec)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            new(@"\b(echo|printf|read|cd|ls|pwd|mkdir|rm|mv|cp|chmod|chown|find|grep|sed|awk|cut|sort|head|tail|cat|curl|wget|git)\b",
                new Dictionary<int, string> { [0] = ScopeName.Type }),
            new(@"\b\d+\b",
                new Dictionary<int, string> { [0] = ScopeName.Number }),
        ];
    }

    private sealed class TomlLang : ILanguage
    {
        public static readonly TomlLang Instance = new();

        public string  Id               => "toml";
        public string  Name             => "TOML";
        public string  CssClassName     => "toml";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            new(@"#[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            // Multi-line strings first
            new(@"'{3}[\s\S]*?'{3}|""{3}[\s\S]*?""{3}",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            new(@"""(?:[^""\\]|\\.)*""|'[^']*'",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Array of tables [[...]] before tables [...]
            new(@"(?m)^\[\[[^\[\]]+\]\]",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            new(@"(?m)^\[[^\[\]]+\]",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            // Keys
            new(@"(?m)^\s*[\w\-\.""']+(?:\s*,\s*[\w\-\.""']+)*(?=\s*=)",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            new(@"\b(true|false)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            new(@"\b\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d[\d_]*)?\b|0x[0-9a-fA-F_]+\b|0o[0-7_]+\b|0b[01_]+\b",
                new Dictionary<int, string> { [0] = ScopeName.Number }),
        ];
    }

    private sealed class DockerfileLang : ILanguage
    {
        public static readonly DockerfileLang Instance = new();

        public string  Id               => "dockerfile";
        public string  Name             => "Dockerfile";
        public string  CssClassName     => "dockerfile";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            new(@"#[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            new(@"""(?:[^""\\]|\\.)*""",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Variables: $VAR and ${VAR}
            new(@"\$\{[^}]*\}|\$[a-zA-Z_][a-zA-Z_0-9]*",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            // Instructions at start of line (case-insensitive via (?i))
            new(@"(?m)(?i)^(FROM|RUN|CMD|LABEL|EXPOSE|ENV|ADD|COPY|ENTRYPOINT|VOLUME|USER|WORKDIR|ARG|ONBUILD|STOPSIGNAL|HEALTHCHECK|SHELL)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
        ];
    }

    private sealed class TSqlLang : ILanguage
    {
        public static readonly TSqlLang Instance = new();

        public string  Id               => "tsql";
        public string  Name             => "T-SQL";
        public string  CssClassName     => "sql";
        public string? FirstLinePattern => null;
        public bool    HasAlias(string alias) => false;

        public IList<LanguageRule> Rules =>
        [
            // Block comments before line comments
            new(@"/\*[\s\S]*?\*/",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            new(@"--[^\n]*",
                new Dictionary<int, string> { [0] = ScopeName.Comment }),
            // N-prefixed and plain string literals
            new(@"N'(?:[^']|'')*'|'(?:[^']|'')*'",
                new Dictionary<int, string> { [0] = ScopeName.String }),
            // Variables and parameters: @name, @@name
            new(@"@@?\w+",
                new Dictionary<int, string> { [0] = ScopeName.PreprocessorKeyword }),
            // Keywords (case-insensitive)
            new(@"(?i)\b(SELECT|INSERT|UPDATE|DELETE|MERGE|INTO|VALUES|OUTPUT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|FULL|OUTER|CROSS|APPLY|ON|AND|OR|NOT|IN|EXISTS|BETWEEN|LIKE|IS|AS|NULL|ALL|ANY|SOME|DISTINCT|TOP|UNION|INTERSECT|EXCEPT|GROUP|BY|ORDER|HAVING|OFFSET|FETCH|NEXT|ROWS|ONLY|WITH|NOLOCK|HOLDLOCK|UPDLOCK|TABLOCKX|TABLOCK|ROWLOCK|READPAST|READUNCOMMITTED|NOEXPAND|CREATE|ALTER|DROP|TRUNCATE|TABLE|VIEW|DATABASE|SCHEMA|PROCEDURE|PROC|FUNCTION|TRIGGER|INDEX|CONSTRAINT|PRIMARY|FOREIGN|KEY|REFERENCES|UNIQUE|CHECK|DEFAULT|IDENTITY|SET|BEGIN|END|GO|USE|EXEC|EXECUTE|IF|ELSE|WHILE|BREAK|CONTINUE|GOTO|RETURN|DECLARE|CURSOR|OPEN|CLOSE|DEALLOCATE|CASE|WHEN|THEN|THROW|TRY|CATCH|RAISERROR|PRINT|TRANSACTION|TRAN|COMMIT|ROLLBACK|SAVE|DISTRIBUTED|PIVOT|UNPIVOT|OVER|PARTITION|ROW_NUMBER|RANK|DENSE_RANK|NTILE|LEAD|LAG|FIRST_VALUE|LAST_VALUE|COALESCE|ISNULL|NULLIF|CAST|CONVERT|IIF|CHOOSE|SCOPE_IDENTITY|OBJECT_ID|OBJECT_NAME|SCHEMA_NAME|DB_NAME|GETDATE|GETUTCDATE|NEWID|NEWSEQUENTIALID|SYSDATETIME|SYSUTCDATETIME)\b",
                new Dictionary<int, string> { [0] = ScopeName.Keyword }),
            // Data types
            new(@"(?i)\b(BIT|TINYINT|SMALLINT|INT|BIGINT|DECIMAL|NUMERIC|MONEY|SMALLMONEY|FLOAT|REAL|DATE|DATETIME|DATETIME2|DATETIMEOFFSET|SMALLDATETIME|TIME|CHAR|VARCHAR|NCHAR|NVARCHAR|TEXT|NTEXT|BINARY|VARBINARY|IMAGE|XML|UNIQUEIDENTIFIER|SQL_VARIANT|HIERARCHYID|GEOGRAPHY|GEOMETRY|ROWVERSION|TIMESTAMP|CURSOR|TABLE|MAX)\b",
                new Dictionary<int, string> { [0] = ScopeName.Type }),
            // Numbers (integers, decimals, hex)
            new(@"\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?\b",
                new Dictionary<int, string> { [0] = ScopeName.Number }),
        ];
    }
}
