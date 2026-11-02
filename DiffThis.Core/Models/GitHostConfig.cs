namespace DiffThis.Core.Models;

/// <summary>
/// A user-configured self-hosted git host (GitHub Enterprise, GitLab self-managed, Bitbucket
/// Server/Data Center, or Gitea/Forgejo), matched against a repository's "origin" remote by
/// <see cref="Host"/>. Authenticated with a Personal Access Token.
/// </summary>
public class GitHostConfig
{
    public string Id   { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;  // e.g. "git.dealx.com" — matched against the remote's hostname
    public GitProviderKind Kind { get; set; } = GitProviderKind.Gitea;
}
