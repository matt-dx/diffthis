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
    /// with the base branch, so both sides are available for a diff. The base side to use is
    /// "origin/{baseRefName}". Returns <paramref name="localRef"/> on success.
    Task<string> FetchPullRequestRefAsync(string repositoryPath, string remoteRef, string localRef, string baseRefName, CancellationToken ct = default);
}
