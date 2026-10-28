using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

/// <summary>
/// Manages the user-configured list of self-hosted git hosts (GitHub Enterprise, GitLab
/// self-managed, Bitbucket Server/Data Center, Gitea/Forgejo) used to recognize a repository's
/// "origin" remote when it isn't one of the well-known cloud hosts.
/// </summary>
public interface IGitHostService
{
    IReadOnlyList<GitHostConfig> Hosts { get; }

    event Action? Changed;

    void AddHost(string name, string host, GitProviderKind kind);
    void UpdateHost(GitHostConfig host);
    void RemoveHost(string hostId);

    /// Case-insensitive lookup by hostname, as parsed from a repository's remote URL.
    GitHostConfig? FindByHost(string host);

    Task<string?> GetTokenAsync(string hostId);
    Task SetTokenAsync(string hostId, string token);
}
