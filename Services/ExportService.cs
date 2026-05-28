using System.Text;
using DiffThis.Models;

namespace DiffThis.Services;

public class ExportService : IExportService
{
    public string GenerateMarkdown(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Diff: `{diff.BaseBranch}` → `{diff.CompareBranch}`");
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

    public async Task ExportMarkdownAsync(DiffResult diff, string filePath)
    {
        await File.WriteAllTextAsync(filePath, GenerateMarkdown(diff), Encoding.UTF8);
    }
}
