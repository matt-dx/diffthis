namespace DiffThis.Core.Models;

public enum PullRequestState { Open, Closed }

/// <summary>
/// Cheap, zero-network-cost indication of whether a PR's source changes will still be fetchable,
/// derived entirely from fields already present on the PR as listed. GitHub, GitLab, and Bitbucket
/// Server all expose a permanent synthetic PR ref, so their PRs are always <see cref="Available"/>.
/// Bitbucket Cloud and Azure DevOps have no such ref — once a PR is closed, its source branch may
/// already be deleted, so compare falls back to the merge commit (<see cref="ReconstructableFromMergeCommit"/>)
/// when one exists, or is simply <see cref="Unknown"/> for a closed-without-merging PR with no
/// merge commit to fall back to.
/// </summary>
public enum PullRequestSourceAvailability { Available, ReconstructableFromMergeCommit, Unknown }

public class PullRequestInfo
{
    public GitProviderKind Provider { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string BaseRefName { get; set; } = string.Empty;
    public string HeadRefName { get; set; } = string.Empty;

    /// The remote ref to fetch for the compare side of the diff, e.g. "refs/pull/9/head"
    /// (GitHub), "refs/merge-requests/9/head" (GitLab), "refs/pull-requests/9/from"
    /// (Bitbucket), or "refs/heads/feature-x" (Azure DevOps, which has no fork-PR ref).
    public string SourceRemoteRef { get; set; } = string.Empty;

    /// The commit created on the base branch when this PR was merged (Bitbucket Cloud's
    /// "merge_commit", Azure DevOps's "lastMergeCommit"). Null when the PR isn't merged, or the
    /// provider doesn't expose one. Used as a last-resort diff source when <see cref="SourceRemoteRef"/>
    /// is no longer fetchable — the merge commit diffed against its own parent reproduces the same
    /// file changes the PR introduced, even after the source branch and its commits are gone.
    public string? MergeCommitSha { get; set; }

    public PullRequestSourceAvailability SourceAvailability { get; set; } = PullRequestSourceAvailability.Available;

    /// The fork's clone URL, set only when <see cref="IsCrossRepository"/> is true and the
    /// provider exposes it (Bitbucket Cloud, Azure DevOps). <see cref="SourceRemoteRef"/> is a
    /// branch name or commit on the FORK, not on "origin" — fetching it from origin would silently
    /// resolve an unrelated same-named ref there instead, or fail outright.
    public string? SourceRemoteUrl { get; set; }

    public bool IsCrossRepository { get; set; }
    public bool IsDraft { get; set; }
    public bool IsMerged { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
