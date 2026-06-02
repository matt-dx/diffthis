using System.Text;
using DiffThis.Models;

namespace DiffThis.Services;

public class ExportService : IExportService
{
    public string GenerateMarkdown(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Diff: `{diff.BaseDisplay}` → `{diff.CompareDisplay}`");
        sb.AppendLine();
        sb.AppendLine($"**Repository:** {diff.RepositoryName}");
        sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"## Summary");
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
            sb.AppendLine($"## `{file.DisplayPath}`");
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

        foreach (var (runKey, entry) in aiResults.OrderBy(kv => kv.Value.CachedAt))
        {
            var modelLabel = runKey.Model switch
            {
                "claude-opus-4-8"           => "Opus 4.8",
                "claude-sonnet-4-6"         => "Sonnet 4.6",
                "claude-haiku-4-5-20251001" => "Haiku 4.5",
                _                           => runKey.Model,
            };
            var featureLabel = runKey.Feature == "review" ? "Code Review" : "Explain Changes";
            sb.AppendLine($"### {featureLabel} — {runKey.TabLabel(modelLabel)}");
            sb.AppendLine();
            sb.AppendLine($"_Cached {entry.CachedAt.ToLocalTime():yyyy-MM-dd HH:mm}_");
            sb.AppendLine();
            sb.AppendLine(entry.Response.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
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
