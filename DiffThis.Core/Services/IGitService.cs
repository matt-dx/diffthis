using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

public interface IGitService
{
    bool IsGitRepository(string path);
    Task<List<string>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default);
    Task<List<CommitInfo>> GetCommitsAsync(string repositoryPath, string branch, int maxCount = 50, CancellationToken ct = default);
    Task<DiffResult> GetDiffAsync(string repositoryPath, string baseBranch, string compareBranch, CancellationToken ct = default, int contextLines = 3);
    Task<bool> FetchAsync(string repositoryPath, CancellationToken ct = default);
    Task<List<DiffHunk>> GetFileHunksAsync(string repositoryPath, string baseBranch, string compareBranch, string filePath, int contextLines = 3, CancellationToken ct = default);

    /// Fetches an arbitrary remote ref (e.g. "refs/pull/9/head", "refs/merge-requests/9/head",
    /// "refs/pull-requests/9/from", or a plain branch ref) into <paramref name="localRef"/>, along
    /// with the base branch, so both sides are available for a diff. On success the result's
    /// CompareRef is normally <paramref name="localRef"/>, diffed against its MergeBase (falling
    /// back to "origin/{baseRefName}" if no merge base could be found).
    /// If the source ref is gone (e.g. a squash merge deleted the branch) and
    /// <paramref name="mergeCommitSha"/> is supplied and still reachable, the result instead points
    /// at that merge commit with a BaseRefOverride of its own parent — diffing those two reproduces
    /// the same file changes without needing the original source commits at all.
    /// When <paramref name="sourceRemoteUrl"/> is supplied (the PR's source is a fork), the compare
    /// ref is fetched from that URL directly instead of "origin", which doesn't have it.
    Task<PullRequestRefResolution> FetchPullRequestRefAsync(string repositoryPath, string remoteRef, string localRef, string baseRefName, string? mergeCommitSha = null, string? sourceRemoteUrl = null, bool isCrossRepository = false, CancellationToken ct = default);
}
