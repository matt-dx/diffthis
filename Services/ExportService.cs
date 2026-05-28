using System.Text;
using System.Web;
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

    public string GenerateHtml(DiffResult diff, bool darkMode)
    {
        var sb = new StringBuilder();
        var theme = darkMode ? "dark" : "light";

        sb.Append($"""
            <!DOCTYPE html>
            <html data-theme="{theme}">
            <head>
            <meta charset="utf-8"/>
            <title>Diff: {HttpUtility.HtmlEncode(diff.BaseBranch)} → {HttpUtility.HtmlEncode(diff.CompareBranch)}</title>
            <style>
            {GetCss()}
            </style>
            </head>
            <body>
            <div class="header">
              <h1>
                <span class="repo">{HttpUtility.HtmlEncode(diff.RepositoryName)}</span>
                <span class="sep">:</span>
                <code>{HttpUtility.HtmlEncode(diff.BaseBranch)}</code>
                <span class="arrow">→</span>
                <code>{HttpUtility.HtmlEncode(diff.CompareBranch)}</code>
              </h1>
              <p class="summary">{diff.Files.Count} files changed &nbsp;
                <span class="add">+{diff.TotalAdditions}</span> &nbsp;
                <span class="del">-{diff.TotalDeletions}</span>
              </p>
            </div>
            """);

        foreach (var (file, idx) in diff.Files.Select((f, i) => (f, i)))
        {
            var statusClass = file.Status switch
            {
                DiffFileStatus.Added => "added",
                DiffFileStatus.Deleted => "deleted",
                DiffFileStatus.Renamed => "renamed",
                _ => "modified"
            };
            var statusLabel = file.Status switch
            {
                DiffFileStatus.Added => "A",
                DiffFileStatus.Deleted => "D",
                DiffFileStatus.Renamed => "R",
                DiffFileStatus.Copied => "C",
                _ => "M"
            };

            sb.Append($"""
                <div class="file" id="file-{idx}">
                  <div class="file-header">
                    <span class="file-status {statusClass}">{statusLabel}</span>
                    <span class="file-dir">{HttpUtility.HtmlEncode(file.FileDirectory)}</span><span class="file-name">{HttpUtility.HtmlEncode(file.FileName)}</span>
                    <span class="file-stats"><span class="add">+{file.Additions}</span> <span class="del">-{file.Deletions}</span></span>
                  </div>
                """);

            if (file.IsBinary)
            {
                sb.Append("<div class=\"binary\">Binary file</div>");
            }
            else if (file.Hunks.Count == 0)
            {
                sb.Append("<div class=\"no-changes\">No textual changes</div>");
            }
            else
            {
                foreach (var hunk in file.Hunks)
                {
                    sb.Append($"""
                        <div class="hunk-header">@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@
                        {(hunk.Context.Length > 0 ? $" <span class=\"hunk-ctx\">{HttpUtility.HtmlEncode(hunk.Context)}</span>" : "")}</div>
                        <table class="diff-table">
                        """);

                    foreach (var line in hunk.Lines)
                    {
                        var rowClass = line.Type switch
                        {
                            DiffLineType.Addition => "add-line",
                            DiffLineType.Deletion => "del-line",
                            _ => "ctx-line"
                        };
                        var prefix = line.Type switch
                        {
                            DiffLineType.Addition => "+",
                            DiffLineType.Deletion => "-",
                            _ => " "
                        };
                        var oldNum = line.OldLineNumber.HasValue ? line.OldLineNumber.Value.ToString() : "";
                        var newNum = line.NewLineNumber.HasValue ? line.NewLineNumber.Value.ToString() : "";

                        sb.Append($"""
                            <tr class="{rowClass}">
                              <td class="ln old">{oldNum}</td>
                              <td class="ln new">{newNum}</td>
                              <td class="sign">{prefix}</td>
                              <td class="code">{HttpUtility.HtmlEncode(line.Content)}</td>
                            </tr>
                            """);
                    }

                    sb.Append("</table>");
                }
            }

            sb.Append("</div>");
        }

        sb.Append($"""
            <div class="footer">Generated by DiffThis · {DateTime.Now:yyyy-MM-dd HH:mm}</div>
            </body></html>
            """);

        return sb.ToString();
    }

    public async Task ExportMarkdownAsync(DiffResult diff, string filePath)
    {
        await File.WriteAllTextAsync(filePath, GenerateMarkdown(diff), Encoding.UTF8);
    }

    public async Task ExportHtmlAsync(DiffResult diff, string filePath, bool darkMode)
    {
        await File.WriteAllTextAsync(filePath, GenerateHtml(diff, darkMode), Encoding.UTF8);
    }

    private static string GetCss() => """
        *, *::before, *::after { box-sizing: border-box; }
        :root {
          --bg: #ffffff; --fg: #24292e; --border: #e1e4e8;
          --file-hdr: #f6f8fa; --hunk-bg: #dbedff; --hunk-fg: #586069;
          --add-bg: #e6ffed; --add-fg: #22863a; --add-ln: #cdffd8;
          --del-bg: #ffeef0; --del-fg: #b31d28; --del-ln: #ffdce0;
          --ctx-fg: #24292e; --ln-fg: #babbbd;
          --header-bg: #24292e; --header-fg: #ffffff;
          --footer-fg: #999;
        }
        [data-theme="dark"] {
          --bg: #0d1117; --fg: #c9d1d9; --border: #30363d;
          --file-hdr: #161b22; --hunk-bg: #1c2a3a; --hunk-fg: #8b949e;
          --add-bg: #0d2b19; --add-fg: #56d364; --add-ln: #0f3324;
          --del-bg: #2b0b0b; --del-fg: #ff7b72; --del-ln: #3a0c0c;
          --ctx-fg: #c9d1d9; --ln-fg: #484f58;
          --header-bg: #161b22; --header-fg: #c9d1d9;
          --footer-fg: #666;
        }
        body { background: var(--bg); color: var(--fg); font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; margin: 0; padding: 0; }
        .header { background: var(--header-bg); color: var(--header-fg); padding: 20px 24px; border-bottom: 1px solid var(--border); }
        .header h1 { margin: 0 0 8px; font-size: 18px; font-weight: 600; }
        .header .repo { font-weight: 700; }
        .header .sep { margin: 0 4px; opacity: .5; }
        .header .arrow { margin: 0 8px; }
        .header code { font-family: monospace; background: rgba(255,255,255,.1); padding: 2px 6px; border-radius: 4px; }
        .summary { margin: 0; font-size: 14px; opacity: .8; }
        .add { color: #3fb950; font-weight: 600; }
        .del { color: #f85149; font-weight: 600; }
        .file { border: 1px solid var(--border); border-radius: 6px; margin: 16px 24px; overflow: hidden; }
        .file-header { background: var(--file-hdr); padding: 8px 12px; display: flex; align-items: center; gap: 8px; font-size: 13px; border-bottom: 1px solid var(--border); }
        .file-status { font-size: 11px; font-weight: 700; padding: 1px 6px; border-radius: 3px; color: #fff; }
        .file-status.added { background: #2ea043; }
        .file-status.deleted { background: #da3633; }
        .file-status.renamed { background: #9a6700; }
        .file-status.modified { background: #1f6feb; }
        .file-dir { font-family: monospace; color: var(--hunk-fg); }
        .file-name { font-family: monospace; font-weight: 600; }
        .file-stats { margin-left: auto; }
        .hunk-header { background: var(--hunk-bg); color: var(--hunk-fg); padding: 4px 12px; font-family: monospace; font-size: 12px; border-bottom: 1px solid var(--border); border-top: 1px solid var(--border); }
        .hunk-ctx { font-style: italic; margin-left: 8px; }
        .diff-table { width: 100%; border-collapse: collapse; font-family: 'Cascadia Code', 'Fira Code', 'Consolas', monospace; font-size: 12px; }
        .diff-table td { padding: 1px 6px; }
        .add-line { background: var(--add-bg); }
        .add-line .code { color: var(--add-fg); }
        .add-line .ln { background: var(--add-ln); }
        .del-line { background: var(--del-bg); }
        .del-line .code { color: var(--del-fg); }
        .del-line .ln { background: var(--del-ln); }
        .ctx-line .code { color: var(--ctx-fg); }
        .ln { color: var(--ln-fg); text-align: right; width: 1%; white-space: nowrap; user-select: none; padding-right: 12px; border-right: 1px solid var(--border); }
        .sign { color: var(--ln-fg); width: 16px; text-align: center; user-select: none; }
        .code { width: 100%; white-space: pre-wrap; overflow-wrap: break-word; }
        .sign { white-space: nowrap; }
        .binary, .no-changes { padding: 16px; color: var(--hunk-fg); font-style: italic; text-align: center; }
        .footer { text-align: center; padding: 24px; color: var(--footer-fg); font-size: 12px; }
        """;
}
