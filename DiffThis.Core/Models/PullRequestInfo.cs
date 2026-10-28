namespace DiffThis.Core.Models;

public enum PullRequestState { Open, Closed }

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

    public bool IsCrossRepository { get; set; }
    public bool IsDraft { get; set; }
    public bool IsMerged { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
