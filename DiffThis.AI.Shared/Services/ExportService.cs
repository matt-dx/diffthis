using System.Text;
using DiffThis.Core.Models;
using DiffThis.AI.Shared.Models;
using DiffThis.AI.Shared.Services;

namespace DiffThis.AI.Shared.Services;

public class ExportService : IExportService
{
    public string GenerateMarkdown(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# DiffThis {diff.RepositoryName}");
        sb.AppendLine();
        sb.AppendLine($"**Comparing:** `{diff.BaseDisplay}` → `{diff.CompareDisplay}`");
        sb.AppendLine($"**Local path:** `{diff.RepositoryPath}`");
        if (diff.RemoteUri.Length > 0)
            sb.AppendLine($"**Remote:** {diff.RemoteUri}");
        sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## Diff");
        sb.AppendLine();
        sb.AppendLine("### Summary");
        sb.AppendLine();
        sb.AppendLine($"{diff.Files.Count} files changed &nbsp; **+{diff.TotalAdditions}** additions &nbsp; **-{diff.TotalDeletions}** deletions");
        sb.AppendLine();
        sb.AppendLine("| File | Status | Additions | Deletions |");
        sb.AppendLine("| --- | --- | ---: | ---: |");

        foreach (var file in diff.Files)
        {
            var badge = file.Status switch
            {
                DiffFileStatus.Added    => "Added",
                DiffFileStatus.Deleted  => "Deleted",
                DiffFileStatus.Renamed  => "Renamed",
                DiffFileStatus.Copied   => "Copied",
                _                       => "Modified"
            };
            sb.AppendLine($"| `{file.DisplayPath}` | {badge} | +{file.Additions} | -{file.Deletions} |");
        }
        sb.AppendLine();

        foreach (var file in diff.Files)
        {
            sb.AppendLine($"### {file.FileName} - `{file.DisplayPath}`");
            sb.AppendLine();

            if (file.IsBinary)
            {
                sb.AppendLine("_Binary file_");
                sb.AppendLine();
                continue;
            }

            foreach (var hunk in file.Hunks)
            {
                sb.AppendLine($"```diff");
                sb.AppendLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@ {hunk.Context}");
                foreach (var line in hunk.Lines)
                {
                    var prefix = line.Type switch
                    {
                        DiffLineType.Addition => "+",
                        DiffLineType.Deletion => "-",
                        _ => " "
                    };
                    sb.AppendLine($"{prefix}{line.Content}");
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public string GenerateMarkdown(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> aiResults)
    {
        var sb = new StringBuilder(GenerateMarkdown(diff));

        if (aiResults.Count == 0) return sb.ToString();

        sb.AppendLine();
        sb.AppendLine("## Analysis");
        sb.AppendLine();

        foreach (var (runKey, entry) in aiResults
            .OrderBy(kv => kv.Key.Feature == "review" ? 1 : 0)
            .ThenBy(kv => kv.Value.CachedAt))
        {
            var modelLabel = runKey.Model switch
            {
                "claude-opus-4-8"           => "Opus 4.8",
                "claude-sonnet-4-6"         => "Sonnet 4.6",
                "claude-haiku-4-5-20251001" => "Haiku 4.5",
                _                           => runKey.Model,
            };
            var sectionLabel = runKey.Feature == "review"
                ? $"Review - {runKey.TabLabel(modelLabel)}"
                : $"Context - {runKey.TabLabel(modelLabel)}";
            sb.AppendLine($"### {sectionLabel}");
            sb.AppendLine();
            sb.AppendLine($"**Time received:** {entry.CachedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine(PromoteHeadings(entry.Response.TrimEnd()));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// Shifts all ATX headings down by three levels (# → ####, ## → #####, etc.), clamped at h6,
    /// so AI sub-headings nest under their ### parent section. Skips lines inside fenced code blocks.
    private static string PromoteHeadings(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (!inFence && line.StartsWith('#'))
            {
                var count = 0;
                while (count < line.Length && line[count] == '#') count++;
                // Only shift valid ATX headings: 1–6 '#' followed by space/tab or end-of-line
                if (count >= 1 && count <= 6 && (count == line.Length || line[count] == ' ' || line[count] == '\t'))
                {
                    var newCount = Math.Min(count + 3, 6);
                    lines[i] = new string('#', newCount) + line[count..];
                }
            }
        }
        return string.Join('\n', lines);
    }

    public async Task ExportMarkdownAsync(DiffResult diff, string filePath)
    {
        await File.WriteAllTextAsync(filePath, GenerateMarkdown(diff), Encoding.UTF8);
    }

    public async Task ExportMarkdownAsync(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> aiResults, string filePath)
    {
        await File.WriteAllTextAsync(filePath, GenerateMarkdown(diff, aiResults), Encoding.UTF8);
    }
}
