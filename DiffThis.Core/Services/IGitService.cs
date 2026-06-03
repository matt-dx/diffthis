using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

public interface IGitService
{
    bool IsGitRepository(string path);
    Task<List<string>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default);
    Task<List<CommitInfo>> GetCommitsAsync(string repositoryPath, string branch, int maxCount = 50, CancellationToken ct = default);
    Task<DiffResult> GetDiffAsync(string repositoryPath, string baseBranch, string compareBranch, CancellationToken ct = default);
    Task<bool> FetchAsync(string repositoryPath, CancellationToken ct = default);
}
