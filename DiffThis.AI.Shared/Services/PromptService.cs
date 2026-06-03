using System.Reflection;
using System.Text;
using DiffThis.Core.Models;

namespace DiffThis.AI.Shared.Services;

/// <summary>
/// Loads and renders prompt templates.
///
/// Resolution order:
///   1. User override: %LOCALAPPDATA%\DiffThis\prompts\{name}.md
///   2. Embedded default: DiffThis.AI.Shared.Prompts.{name}.md
///
/// Templates use {{Variable}} placeholders (case-sensitive).
/// </summary>
public class PromptService
{
    private static readonly string UserPromptDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DiffThis", "prompts");

    public string BuildReviewPrompt(DiffResult diff, int maxDiffChars = 60_000)
        => Render(LoadTemplate("review"), diff, maxDiffChars);

    public string BuildExplainPrompt(DiffResult diff, int maxDiffChars = 60_000)
        => Render(LoadTemplate("explain"), diff, maxDiffChars);

    /// <summary>
    /// Returns the prompt split into (System, User) parts for APIs that accept
    /// separate system and user messages.
    /// <para>
    /// <c>System</c> contains the instructions and diff metadata header (no diff lines).
    /// <c>User</c>   contains only the raw diff content.
    /// </para>
    /// </summary>
    public (string System, string User) BuildReviewPromptParts(DiffResult diff, int maxDiffChars = 60_000)
        => RenderParts(LoadTemplate("review"), diff, maxDiffChars);

    /// <inheritdoc cref="BuildReviewPromptParts"/>
    public (string System, string User) BuildExplainPromptParts(DiffResult diff, int maxDiffChars = 60_000)
        => RenderParts(LoadTemplate("explain"), diff, maxDiffChars);

    // ── Template loading ──────────────────────────────────────────────────

    private static string LoadTemplate(string name)
    {
        var userFile = Path.Combine(UserPromptDir, $"{name}.md");
        if (File.Exists(userFile))
            return File.ReadAllText(userFile);

        var resourceName = $"DiffThis.AI.Shared.Prompts.{name}.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    private static string Render(string template, DiffResult diff, int maxDiffChars)
    {
        var diffContent = BuildDiffContent(diff, maxDiffChars);
        return template
            .Replace("{{RepositoryName}}", diff.RepositoryName)
            .Replace("{{BaseDisplay}}", diff.BaseDisplay)
            .Replace("{{CompareDisplay}}", diff.CompareDisplay)
            .Replace("{{FileCount}}", diff.Files.Count.ToString())
            .Replace("{{Additions}}", diff.TotalAdditions.ToString())
            .Replace("{{Deletions}}", diff.TotalDeletions.ToString())
            .Replace("{{FileList}}", BuildFileList(diff))
            .Replace("{{DiffContent}}", diffContent);
    }

    /// <summary>
    /// Splits a template into a system part (instructions + metadata, no diff lines)
    /// and a user part (raw diff content only).
    /// </summary>
    private static (string System, string User) RenderParts(string template, DiffResult diff, int maxDiffChars)
    {
        var diffContent = BuildDiffContent(diff, maxDiffChars);

        // Take only the portion of the template before {{DiffContent}} as the system
        // message, so any text that follows the placeholder is not included.
        var splitIndex = template.IndexOf("{{DiffContent}}", StringComparison.Ordinal);
        var systemTemplate = splitIndex >= 0 ? template[..splitIndex] : template;

        var system = systemTemplate
            .Replace("{{RepositoryName}}", diff.RepositoryName)
            .Replace("{{BaseDisplay}}", diff.BaseDisplay)
            .Replace("{{CompareDisplay}}", diff.CompareDisplay)
            .Replace("{{FileCount}}", diff.Files.Count.ToString())
            .Replace("{{Additions}}", diff.TotalAdditions.ToString())
            .Replace("{{Deletions}}", diff.TotalDeletions.ToString())
            .Replace("{{FileList}}", BuildFileList(diff))
            .TrimEnd();

        return (system, diffContent);
    }

    private static string BuildFileList(DiffResult diff)
    {
        var sb = new StringBuilder();
        foreach (var file in diff.Files)
        {
            var status = file.Status switch
            {
                DiffFileStatus.Added   => "added",
                DiffFileStatus.Deleted => "deleted",
                DiffFileStatus.Renamed => "renamed",
                DiffFileStatus.Copied  => "copied",
                _                      => "modified",
            };
            var lang = DetectLanguage(file.DisplayPath);
            sb.AppendLine($"- {file.DisplayPath} ({status}{(lang.Length > 0 ? $", {lang}" : "")})");
        }
        return sb.ToString().TrimEnd();
    }

    private static string DetectLanguage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs"         => "C#",
            ".fs" or ".fsx" => "F#",
            ".vb"         => "VB.NET",
            ".ts" or ".tsx" => "TypeScript",
            ".js" or ".jsx" => "JavaScript",
            ".py"         => "Python",
            ".go"         => "Go",
            ".rs"         => "Rust",
            ".java"       => "Java",
            ".kt" or ".kts" => "Kotlin",
            ".swift"      => "Swift",
            ".cpp" or ".cc" or ".cxx" => "C++",
            ".c"          => "C",
            ".h" or ".hpp" => "C/C++ header",
            ".rb"         => "Ruby",
            ".php"        => "PHP",
            ".ex" or ".exs" => "Elixir",
            ".dart"       => "Dart",
            ".lua"        => "Lua",
            ".r"          => "R",
            ".m"          => "Objective-C",
            ".html" or ".htm" => "HTML",
            ".css"        => "CSS",
            ".scss" or ".sass" => "SCSS",
            ".less"       => "Less",
            ".vue"        => "Vue",
            ".razor"      => "Razor",
            ".json"       => "JSON",
            ".yaml" or ".yml" => "YAML",
            ".toml"       => "TOML",
            ".xml"        => "XML",
            ".sql"        => "SQL",
            ".sh" or ".bash" => "Shell",
            ".ps1" or ".psm1" => "PowerShell",
            ".tf" or ".tfvars" => "Terraform",
            ".proto"      => "Protocol Buffers",
            ".graphql" or ".gql" => "GraphQL",
            ".md" or ".mdx" => "Markdown",
            _             => "",
        };
    }

    private static string BuildDiffContent(DiffResult diff, int maxChars)
    {
        var sb = new StringBuilder();
        var written = 0;
        var truncated = false;

        foreach (var file in diff.Files)
        {
            if (truncated) break;
            if (written >= maxChars) { sb.AppendLine("\n... (diff truncated due to length)"); break; }
            sb.AppendLine($"--- {file.DisplayPath}");

            if (file.IsBinary) { sb.AppendLine("[binary file]"); continue; }

            foreach (var hunk in file.Hunks)
            {
                if (truncated) break;
                var ctx = hunk.Context.Length > 0 ? " " + hunk.Context : "";
                sb.AppendLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@{ctx}");

                foreach (var line in hunk.Lines)
                {
                    var sign = line.Type switch
                    {
                        DiffLineType.Addition => "+",
                        DiffLineType.Deletion => "-",
                        _                     => " ",
                    };
                    sb.AppendLine($"{sign}{line.Content}");
                    written += line.Content.Length + 3;
                    if (written < maxChars) continue;
                    sb.AppendLine("\n... (diff truncated due to length)");
                    truncated = true;
                    break;
                }
            }
        }

        return sb.ToString();
    }
}
