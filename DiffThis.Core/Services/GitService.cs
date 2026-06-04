using System.Text;
using CliWrap;
using CliWrap.Buffered;
using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

public class GitService : IGitService
{
    public bool IsGitRepository(string path) =>
        Directory.Exists(path) &&
        (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    public async Task<List<CommitInfo>> GetCommitsAsync(string repositoryPath, string branch, int maxCount = 50, CancellationToken ct = default)
    {
        const char sep = '\x1f';
        var result = await Cli.Wrap("git")
            .WithArguments(["log", branch, $"--format=%H{sep}%h{sep}%s{sep}%an{sep}%ar", $"--max-count={maxCount}"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct);

        if (result.ExitCode != 0) return [];

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Split(sep);
                if (parts.Length < 5) return null;
                return new CommitInfo
                {
                    Hash         = parts[0].Trim(),
                    ShortHash    = parts[1].Trim(),
                    Subject      = parts[2].Trim(),
                    Author       = parts[3].Trim(),
                    RelativeDate = parts[4].Trim()
                };
            })
            .OfType<CommitInfo>()
            .ToList();
    }

    public async Task<List<string>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("git")
            .WithArguments(["branch", "-a", "--format=%(refname:short)"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0) return [];

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !b.StartsWith("HEAD ->"))
            .Distinct()
            .OrderBy(b => b.StartsWith("origin/") ? 1 : 0)
            .ThenBy(b => b)
            .ToList();
    }

    public async Task<bool> FetchAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("git")
            .WithArguments(["fetch", "--prune"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        return result.ExitCode == 0;
    }

    public async Task<DiffResult> GetDiffAsync(string repositoryPath, string baseBranch, string compareBranch, CancellationToken ct = default, int contextLines = 3)
    {
        contextLines = Math.Clamp(contextLines, 0, 200); // >200 lines of context produces diffs that blow past the AI prompt size limit
        var repoName = Path.GetFileName(repositoryPath.TrimEnd('/', '\\'));

        var remoteResult = await Cli.Wrap("git")
            .WithArguments(["remote", "get-url", "origin"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct);
        var remoteUri = remoteResult.ExitCode == 0 ? StripCredentials(remoteResult.StandardOutput.Trim()) : string.Empty;

        var statResult = await Cli.Wrap("git")
            .WithArguments(["diff", "--numstat", $"{baseBranch}..{compareBranch}"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct);

        if (statResult.ExitCode != 0)
            throw new InvalidOperationException(
                statResult.StandardError.Trim() is { Length: > 0 } err ? err
                : $"git diff --numstat exited with code {statResult.ExitCode}");

        var nameStatusResult = await Cli.Wrap("git")
            .WithArguments(["diff", "--name-status", $"{baseBranch}..{compareBranch}"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct);

        var diffResult = await Cli.Wrap("git")
            .WithArguments(["diff", $"--unified={contextLines}", $"{baseBranch}..{compareBranch}"])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct);

        var statMap = ParseNumstat(statResult.StandardOutput);
        var statusMap = ParseNameStatus(nameStatusResult.StandardOutput);
        var files = await Task.Run(() => ParseUnifiedDiff(diffResult.StandardOutput, statMap, statusMap));

        return new DiffResult
        {
            RepositoryPath = repositoryPath,
            RepositoryName = repoName,
            RemoteUri = remoteUri,
            BaseBranch = baseBranch,
            CompareBranch = compareBranch,
            Files = files
        };
    }

    public async Task<List<DiffHunk>> GetFileHunksAsync(string repositoryPath, string baseBranch, string compareBranch, string filePath, int contextLines = 3, CancellationToken ct = default)
    {
        contextLines = Math.Clamp(contextLines, 0, 100_000);
        var result = await Cli.Wrap("git")
            .WithArguments(["diff", $"--unified={contextLines}", $"{baseBranch}..{compareBranch}", "--", filePath])
            .WithWorkingDirectory(repositoryPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)) return [];
        var files = await Task.Run(() => ParseUnifiedDiff(result.StandardOutput, [], []), ct);
        return files.FirstOrDefault()?.Hunks ?? [];
    }

    private static Dictionary<string, (int additions, int deletions)> ParseNumstat(string output)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (int.TryParse(parts[0], out var add) && int.TryParse(parts[1], out var del))
                result[parts[2].Trim()] = (add, del);
        }
        return result;
    }

    private static Dictionary<string, DiffFileStatus> ParseNameStatus(string output)
    {
        var result = new Dictionary<string, DiffFileStatus>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2) continue;
            var status = line[0] switch
            {
                'A' => DiffFileStatus.Added,
                'D' => DiffFileStatus.Deleted,
                'R' => DiffFileStatus.Renamed,
                'C' => DiffFileStatus.Copied,
                _ => DiffFileStatus.Modified
            };
            var parts = line[1..].Trim().Split('\t');
            var path = parts.Length > 1 ? parts[1] : parts[0];
            result[path.Trim()] = status;
        }
        return result;
    }

    private static List<DiffFile> ParseUnifiedDiff(
        string diffOutput,
        Dictionary<string, (int additions, int deletions)> statMap,
        Dictionary<string, DiffFileStatus> statusMap)
    {
        var files = new List<DiffFile>();
        DiffFile? currentFile = null;
        DiffHunk? currentHunk = null;
        int oldLine = 0, newLine = 0;

        foreach (var rawLine in diffOutput.Split('\n'))
        {
            if (rawLine.StartsWith("diff --git "))
            {
                if (currentFile != null) files.Add(currentFile);
                currentHunk = null;
                currentFile = new DiffFile();
                continue;
            }

            if (currentFile == null) continue;

            if (rawLine.StartsWith("--- a/") || rawLine.StartsWith("--- /dev/null"))
            {
                currentFile.OldPath = rawLine.StartsWith("--- a/") ? rawLine[6..].TrimEnd() : string.Empty;
                continue;
            }
            if (rawLine.StartsWith("+++ b/") || rawLine.StartsWith("+++ /dev/null"))
            {
                currentFile.NewPath = rawLine.StartsWith("+++ b/") ? rawLine[6..].TrimEnd() : string.Empty;
                var key = currentFile.NewPath.Length > 0 ? currentFile.NewPath : currentFile.OldPath;
                if (statMap.TryGetValue(key, out var stats))
                {
                    currentFile.Additions = stats.additions;
                    currentFile.Deletions = stats.deletions;
                }
                if (statusMap.TryGetValue(key, out var fileStatus))
                    currentFile.Status = fileStatus;
                else if (currentFile.NewPath.Length == 0) currentFile.Status = DiffFileStatus.Deleted;
                else if (currentFile.OldPath.Length == 0) currentFile.Status = DiffFileStatus.Added;
                continue;
            }

            if (rawLine.StartsWith("Binary files"))
            {
                currentFile.IsBinary = true;
                continue;
            }

            if (rawLine.StartsWith("@@ "))
            {
                currentHunk = ParseHunkHeader(rawLine);
                if (currentHunk != null)
                {
                    currentFile.Hunks.Add(currentHunk);
                    oldLine = currentHunk.OldStart;
                    newLine = currentHunk.NewStart;
                }
                continue;
            }

            if (currentHunk == null || rawLine.Length == 0) continue;

            if (rawLine.StartsWith("-"))
            {
                currentHunk.Lines.Add(new DiffLine { Type = DiffLineType.Deletion, Content = rawLine[1..], OldLineNumber = oldLine++ });
            }
            else if (rawLine.StartsWith("+"))
            {
                currentHunk.Lines.Add(new DiffLine { Type = DiffLineType.Addition, Content = rawLine[1..], NewLineNumber = newLine++ });
            }
            else if (rawLine.StartsWith(" "))
            {
                currentHunk.Lines.Add(new DiffLine { Type = DiffLineType.Context, Content = rawLine[1..], OldLineNumber = oldLine++, NewLineNumber = newLine++ });
            }
            // Lines starting with '\' are git markers like "\ No newline at end of file" — skip them
        }

        if (currentFile != null) files.Add(currentFile);
        return files;
    }

    private static DiffHunk? ParseHunkHeader(string line)
    {
        // Format: @@ -oldStart,oldCount +newStart,newCount @@ context
        try
        {
            var atAt = line.IndexOf(" @@", 3);
            var inner = line[3..(atAt > 0 ? atAt : line.Length)].Trim();
            var context = atAt > 0 ? line[(atAt + 3)..].Trim() : string.Empty;

            var parts = inner.Split(' ');
            if (parts.Length < 2) return null;

            var (oldStart, oldCount) = ParseRange(parts[0].TrimStart('-'));
            var (newStart, newCount) = ParseRange(parts[1].TrimStart('+'));

            return new DiffHunk
            {
                OldStart = oldStart, OldCount = oldCount,
                NewStart = newStart, NewCount = newCount,
                Context = context
            };
        }
        catch { return null; }
    }

    private static (int start, int count) ParseRange(string range)
    {
        var parts = range.Split(',');
        return (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 1);
    }

    private static string StripCredentials(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.UserInfo))
        {
            var builder = new UriBuilder(parsed) { UserName = string.Empty, Password = string.Empty };
            return builder.Uri.ToString().TrimEnd('/') + (uri.EndsWith('/') ? "/" : "");
        }
        return uri;
    }
}
