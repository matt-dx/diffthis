using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

public class GitService : IGitService
{
    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

    // Two `git fetch` processes against the same repo race to update the same
    // refs/remotes/origin/* refs (git uses a compare-and-swap ref update), which fails with
    // "cannot lock ref ... is at X but expected Y". This can happen for real here: the branches
    // page kicks off a background FetchAndRefreshBranchesAsync, and selecting a PR to compare
    // can trigger FetchPullRequestRefAsync before that background fetch finishes. Serialize all
    // fetches per repository path to avoid the race.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim GetFetchLock(string repositoryPath) =>
        _fetchLocks.GetOrAdd(Path.GetFullPath(repositoryPath), _ => new SemaphoreSlim(1, 1));

    // CreateNoWindow = true prevents Windows from opening a console window when git.exe
    // (a console subsystem binary) is spawned from a GUI process such as MAUI. Without
    // it, git — and the internal helper processes it spawns (e.g. git config --null --list)
    // — allocate a new console window and emit broken-pipe errors on the inherited handles.
    private static async Task<GitResult> RunGitAsync(
        string workingDir, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Callers (e.g. the per-repo fetch lock) rely on the process having actually
            // stopped touching the working directory/refs once this call returns, even on
            // cancellation — so kill it and wait for the real exit instead of abandoning it.
            try { proc.Kill(entireProcessTree: true); } catch { }
            await proc.WaitForExitAsync(CancellationToken.None);
            // stdoutTask/stderrTask were reading against `ct` too and may still be unobserved;
            // drain them before `using var proc` disposes the streams out from under them.
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            throw;
        }

        return new GitResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }

    public bool IsGitRepository(string path) =>
        Directory.Exists(path) &&
        (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    public async Task<List<CommitInfo>> GetCommitsAsync(string repositoryPath, string branch, int maxCount = 50, CancellationToken ct = default)
    {
        const char sep = '\x1f';
        var result = await RunGitAsync(repositoryPath,
            ["log", branch, $"--format=%H{sep}%h{sep}%s{sep}%an{sep}%ar", $"--max-count={maxCount}"], ct);

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
        var result = await RunGitAsync(repositoryPath,
            ["branch", "-a", "--format=%(refname:short)"], ct);

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
        var gate = GetFetchLock(repositoryPath);
        await gate.WaitAsync(ct);
        try
        {
            var result = await RunGitAsync(repositoryPath, ["fetch", "--prune"], ct);
            return result.ExitCode == 0;
        }
        finally { gate.Release(); }
    }

    public async Task<DiffResult> GetDiffAsync(string repositoryPath, string baseBranch, string compareBranch, CancellationToken ct = default, int contextLines = 3)
    {
        contextLines = Math.Clamp(contextLines, 0, 200); // >200 lines of context produces diffs that blow past the AI prompt size limit
        var repoName = Path.GetFileName(repositoryPath.TrimEnd('/', '\\'));

        var remoteResult = await RunGitAsync(repositoryPath, ["remote", "get-url", "origin"], ct);
        var remoteUri = remoteResult.ExitCode == 0 ? StripCredentials(remoteResult.StandardOutput.Trim()) : string.Empty;

        var statResult = await RunGitAsync(repositoryPath,
            ["diff", "--numstat", $"{baseBranch}..{compareBranch}"], ct);

        if (statResult.ExitCode != 0)
            throw new InvalidOperationException(
                statResult.StandardError.Trim() is { Length: > 0 } err ? err
                : $"git diff --numstat exited with code {statResult.ExitCode}");

        var nameStatusResult = await RunGitAsync(repositoryPath,
            ["diff", "--name-status", $"{baseBranch}..{compareBranch}"], ct);

        var diffResult = await RunGitAsync(repositoryPath,
            ["diff", $"--unified={contextLines}", $"{baseBranch}..{compareBranch}"], ct);

        var statMap = ParseNumstat(statResult.StandardOutput);
        var statusMap = ParseNameStatus(nameStatusResult.StandardOutput);
        var files = await Task.Run(() => ParseUnifiedDiff(diffResult.StandardOutput, statMap, statusMap));
        var truncatedCount = files.Count(f => f.IsTruncated);

        return new DiffResult
        {
            RepositoryPath = repositoryPath,
            RepositoryName = repoName,
            RemoteUri = remoteUri,
            BaseBranch = baseBranch,
            CompareBranch = compareBranch,
            ContextLines = contextLines,
            Files = files,
            IsTruncated = truncatedCount > 0,
            TruncatedFileCount = truncatedCount,
        };
    }

    public async Task<PullRequestRefResolution> FetchPullRequestRefAsync(string repositoryPath, string remoteRef, string localRef, string baseRefName, string? mergeCommitSha = null, string? sourceRemoteUrl = null, bool isCrossRepository = false, string? sourcePinnedCommit = null, CancellationToken ct = default)
    {
        // A synthetic PR/MR ref (refs/pull/N/head, refs/merge-requests/N/head,
        // refs/pull-requests/N/from) is always fetchable from the destination's own "origin", even
        // for a fork PR — GitHub/GitLab/Gitea/Bitbucket Server keep it there regardless of where
        // the source branch actually lives. An ordinary branch ref is NOT exempt just because it
        // happens to start with "refs/" too — Azure DevOps reports an active fork's head as
        // "refs/heads/...", which "origin" does not have. Only these specific namespaces are safe.
        var isSyntheticPrRef =
            remoteRef.StartsWith("refs/pull/", StringComparison.Ordinal) ||
            remoteRef.StartsWith("refs/merge-requests/", StringComparison.Ordinal) ||
            remoteRef.StartsWith("refs/pull-requests/", StringComparison.Ordinal);

        if (isCrossRepository && string.IsNullOrEmpty(sourceRemoteUrl) && !isSyntheticPrRef)
            throw new InvalidOperationException(
                "This pull request's source is a fork, but its clone URL couldn't be determined, " +
                "so its changes can't be fetched.");

        var gate = GetFetchLock(repositoryPath);
        await gate.WaitAsync(ct);
        try
        {
            return await FetchPullRequestRefCoreAsync(repositoryPath, remoteRef, localRef, baseRefName, mergeCommitSha, sourceRemoteUrl, sourcePinnedCommit, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<PullRequestRefResolution> FetchPullRequestRefCoreAsync(string repositoryPath, string remoteRef, string localRef, string baseRefName, string? mergeCommitSha, string? sourceRemoteUrl, string? sourcePinnedCommit, CancellationToken ct)
    {
        GitResult result;
        if (!string.IsNullOrEmpty(sourceRemoteUrl))
        {
            // The PR's source branch lives in a different repository (a fork) — "origin" doesn't
            // have it. Always fetch the base branch from origin first, regardless of whether the
            // fork fetch below succeeds, so the merge-commit fallback below always has an
            // up-to-date local base branch to check against.
            await RunGitAsync(repositoryPath, ["fetch", "origin", $"refs/heads/{baseRefName}:refs/remotes/origin/{baseRefName}"], ct);
            result = await RunGitAsync(repositoryPath, ["fetch", sourceRemoteUrl, $"{remoteRef}:refs/remotes/{localRef}"], ct);
        }
        else
        {
            result = await RunGitAsync(repositoryPath,
                ["fetch", "origin",
                 $"{remoteRef}:refs/remotes/{localRef}",
                 $"refs/heads/{baseRefName}:refs/remotes/origin/{baseRefName}"], ct);
        }

        if (result.ExitCode == 0)
        {
            var mergeBase = await GetMergeBaseAsync(repositoryPath, $"origin/{baseRefName}", localRef, ct);

            // localRef's own merge-base is itself exactly when the base branch's history now
            // literally incorporates it by reference — a real 2-parent merge commit (localRef
            // becomes its second parent) or a plain fast-forward (the base's tip becomes localRef
            // outright). Diffing it against itself would show nothing. Squash and rebase merges
            // instead create brand-new commits that never reference localRef's original commits,
            // so it stays outside the base branch's ancestry and the plain merge-base below
            // already isolates the PR's own changes correctly — using mergeCommitSha^ there too
            // would be wrong (for rebase specifically, it's just the second-to-last rebased
            // commit, silently dropping every earlier commit's changes from the diff).
            if (mergeBase == localRef && mergeCommitSha is { Length: > 0 })
            {
                var verifyMerge = await RunGitAsync(repositoryPath, ["cat-file", "-e", $"{mergeCommitSha}^{{commit}}"], ct);
                if (verifyMerge.ExitCode == 0) return new PullRequestRefResolution(mergeCommitSha, $"{mergeCommitSha}^");
            }

            // For a closed-without-merging PR (declined/abandoned), the source branch may have
            // moved on or been reused since — pin to the exact commit the PR actually contained
            // when it was open, if the provider recorded one and it's still reachable.
            var compareRef = localRef;
            if (sourcePinnedCommit is { Length: > 0 })
            {
                var verifyPinned = await RunGitAsync(repositoryPath, ["cat-file", "-e", $"{sourcePinnedCommit}^{{commit}}"], ct);
                if (verifyPinned.ExitCode == 0)
                {
                    compareRef = sourcePinnedCommit;
                    mergeBase  = await GetMergeBaseAsync(repositoryPath, $"origin/{baseRefName}", compareRef, ct);
                }
            }

            return new PullRequestRefResolution(compareRef, null, mergeBase);
        }

        var err = result.StandardError.Trim();

        // Some hosts (seen on Azure DevOps) refuse to fetch an arbitrary commit SHA directly,
        // even one that's perfectly reachable — only named refs are fetchable. The base-branch
        // refspec above is a separate part of the same command, so if remoteRef is a SHA and it
        // was actually merged (a real merge, not a squash), it likely came along for free as
        // part of that branch's history. Use it directly in that case — no local ref needed.
        if (IsCommitSha(remoteRef))
        {
            var verify = await RunGitAsync(repositoryPath, ["cat-file", "-e", $"{remoteRef}^{{commit}}"], ct);
            if (verify.ExitCode == 0) return new PullRequestRefResolution(remoteRef, null);
        }

        // Last resort for a squash merge: the original source commits are gone for good (squashing
        // never keeps them reachable anywhere, unlike a real merge), but the squash commit itself —
        // sitting right there on the base branch — contains exactly the same file changes. Diffing
        // it against its own parent reproduces the PR's diff without needing the source at all.
        if (mergeCommitSha is { Length: > 0 })
        {
            var verifyMerge = await RunGitAsync(repositoryPath, ["cat-file", "-e", $"{mergeCommitSha}^{{commit}}"], ct);
            if (verifyMerge.ExitCode != 0)
            {
                await RunGitAsync(repositoryPath, ["fetch", "origin", $"refs/heads/{baseRefName}:refs/remotes/origin/{baseRefName}"], ct);
                verifyMerge = await RunGitAsync(repositoryPath, ["cat-file", "-e", $"{mergeCommitSha}^{{commit}}"], ct);
            }
            if (verifyMerge.ExitCode == 0)
                return new PullRequestRefResolution(mergeCommitSha, $"{mergeCommitSha}^");
        }

        if (err.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This pull request's source commit is no longer available — likely because its " +
                "branch was deleted after merging (e.g. a squash merge) and the host doesn't keep " +
                $"the original commits reachable any other way. ({err})");

        throw new InvalidOperationException(err is { Length: > 0 } ? err : $"git fetch exited with code {result.ExitCode}");
    }

    private static bool IsCommitSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    // The base branch keeps moving after a PR forks off it, so diffing against its current tip
    // drags in every unrelated commit merged since as spurious deletions. The merge-base (fork
    // point) isolates just the PR's own changes. Returns null (falling back to the raw base ref)
    // if it can't be determined, e.g. unrelated histories.
    private static async Task<string?> GetMergeBaseAsync(string repositoryPath, string baseRef, string compareRef, CancellationToken ct)
    {
        var result = await RunGitAsync(repositoryPath, ["merge-base", baseRef, compareRef], ct);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task<List<DiffHunk>> GetFileHunksAsync(string repositoryPath, string baseBranch, string compareBranch, string filePath, int contextLines = 3, CancellationToken ct = default)
    {
        contextLines = Math.Clamp(contextLines, 0, 100_000);
        var result = await RunGitAsync(repositoryPath,
            ["diff", $"--unified={contextLines}", $"{baseBranch}..{compareBranch}", "--", filePath], ct);

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

    // ~50 k rendered diff lines keeps the Blazor IPC batch well under the JSON size limit.
    private const int MaxDiffLines = 50_000;

    private static List<DiffFile> ParseUnifiedDiff(
        string diffOutput,
        Dictionary<string, (int additions, int deletions)> statMap,
        Dictionary<string, DiffFileStatus> statusMap)
    {
        var files = new List<DiffFile>();
        DiffFile? currentFile = null;
        DiffHunk? currentHunk = null;
        int oldLine = 0, newLine = 0;
        int totalLines = 0;
        bool lineLimitHit = false;

        foreach (var rawLine in diffOutput.Split('\n'))
        {
            if (rawLine.StartsWith("diff --git "))
            {
                if (currentFile != null) files.Add(currentFile);
                currentHunk = null;
                currentFile = new DiffFile();
                if (lineLimitHit) currentFile.IsTruncated = true;
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

            // Skip hunk content for files that start after the line limit is hit.
            if (lineLimitHit) continue;

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

            if (++totalLines >= MaxDiffLines)
            {
                lineLimitHit = true;
                currentFile.IsTruncated = true;
                currentHunk = null; // don't add more lines to the open hunk
            }
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
