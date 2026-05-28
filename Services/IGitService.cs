using DiffThis.Models;

namespace DiffThis.Services;

public interface IGitService
{
    bool IsGitRepository(string path);
    Task<List<string>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default);
    Task<DiffResult> GetDiffAsync(string repositoryPath, string baseBranch, string compareBranch, CancellationToken ct = default);
}
