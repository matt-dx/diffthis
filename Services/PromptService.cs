using System.Reflection;
using System.Text;
using DiffThis.Models;

namespace DiffThis.Services;

/// <summary>
/// Loads and renders prompt templates.
///
/// Resolution order:
///   1. User override: %LOCALAPPDATA%\DiffThis\prompts\{name}.md
///   2. Embedded default: DiffThis.Prompts.{name}.md
///
/// Templates use {{Variable}} placeholders (case-sensitive).
/// </summary>
public class PromptService
{
    private static readonly string UserPromptDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DiffThis", "prompts");

    public string BuildReviewPrompt(DiffResult diff)
        => Render(LoadTemplate("review"), diff);

    public string BuildExplainPrompt(DiffResult diff)
        => Render(LoadTemplate("explain"), diff);

    // ── Template loading ──────────────────────────────────────────────────

    private static string LoadTemplate(string name)
    {
        var userFile = Path.Combine(UserPromptDir, $"{name}.md");
        if (File.Exists(userFile))
            return File.ReadAllText(userFile);

        var resourceName = $"DiffThis.Prompts.{name}.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    private static string Render(string template, DiffResult diff)
    {
        var diffContent = BuildDiffContent(diff);
        return template
            .Replace("{{RepositoryName}}", diff.RepositoryName)
            .Replace("{{BaseDisplay}}", diff.BaseDisplay)
            .Replace("{{CompareDisplay}}", diff.CompareDisplay)
            .Replace("{{FileCount}}", diff.Files.Count.ToString())
            .Replace("{{Additions}}", diff.TotalAdditions.ToString())
            .Replace("{{Deletions}}", diff.TotalDeletions.ToString())
            .Replace("{{DiffContent}}", diffContent);
    }

    private static string BuildDiffContent(DiffResult diff)
    {
        const int maxChars = 60_000;
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
