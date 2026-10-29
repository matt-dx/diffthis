namespace DiffThis.Core.Models;

/// <summary>
/// Result of resolving a pull request's compare ref for diffing. When <see cref="BaseRefOverride"/>
/// is set, the caller should diff against it instead of the PR's normal base branch — this happens
/// when the original source commit is gone entirely (e.g. a squash merge deleted the source branch)
/// and the diff is instead reconstructed from the merge/squash commit against its own parent, which
/// reproduces the exact same file changes.
/// </summary>
public sealed record PullRequestRefResolution(string CompareRef, string? BaseRefOverride);
