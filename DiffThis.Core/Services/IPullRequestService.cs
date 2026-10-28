using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

/// <param name="GitHostId">Non-null only for a self-hosted provider already registered under
/// Settings → Git Hosts — the ID to use with <see cref="IGitHostService"/> to read/save its token.</param>
public record PullRequestProviderInfo(GitProviderKind Kind, string Host, string? GitHostId);

public interface IPullRequestService
{
    Task<List<PullRequestInfo>> GetPullRequestsAsync(string repositoryPath, PullRequestState state, CancellationToken ct = default);

    /// Resolves which provider a repository's remote belongs to, without making any API call.
    /// Returns null when the host is unrecognized and not configured under Settings → Git Hosts.
    Task<PullRequestProviderInfo?> DetectProviderAsync(string repositoryPath, CancellationToken ct = default);
}
