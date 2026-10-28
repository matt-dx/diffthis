namespace DiffThis.Core.Models;

/// <summary>
/// Which hosting provider a repository's "origin" remote points to, and therefore which
/// pull-request API shape and authentication method applies.
/// </summary>
public enum GitProviderKind
{
    GitHub,             // github.com — OAuth Device Flow
    GitLab,             // gitlab.com — OAuth Device Flow
    BitbucketCloud,     // bitbucket.org — username + App Password
    AzureDevOps,        // dev.azure.com / *.visualstudio.com — Personal Access Token
    GitHubEnterprise,   // self-hosted — Personal Access Token
    GitLabSelfManaged,  // self-hosted — Personal Access Token
    BitbucketServer,    // self-hosted (Data Center) — Personal Access Token
    Gitea,              // self-hosted (or Forgejo) — Personal Access Token
}

public static class GitProviderKindExtensions
{
    public static string DisplayName(this GitProviderKind kind) => kind switch
    {
        GitProviderKind.GitHub             => "GitHub",
        GitProviderKind.GitLab             => "GitLab",
        GitProviderKind.BitbucketCloud     => "Bitbucket",
        GitProviderKind.AzureDevOps        => "Azure DevOps",
        GitProviderKind.GitHubEnterprise   => "GitHub Enterprise",
        GitProviderKind.GitLabSelfManaged  => "GitLab (self-managed)",
        GitProviderKind.BitbucketServer    => "Bitbucket Server/Data Center",
        GitProviderKind.Gitea              => "Gitea / Forgejo",
        _ => kind.ToString(),
    };
}
