namespace DiffThis.Core.Models;

public class DiffResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string RemoteUri { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = string.Empty;
    public string CompareBranch { get; set; } = string.Empty;

    /// Human-readable labels shown in the UI — includes branch name + short hash when a specific commit is pinned.
    public string BaseLabel    { get; set; } = string.Empty;
    public string CompareLabel { get; set; } = string.Empty;

    public string BaseDisplay    => BaseLabel.Length    > 0 ? BaseLabel    : BaseBranch;
    public string CompareDisplay => CompareLabel.Length > 0 ? CompareLabel : CompareBranch;
    public int ContextLines { get; set; } = 3;
    public List<DiffFile> Files { get; set; } = [];

    /// True when the diff was truncated due to exceeding the line-count limit.
    public bool IsTruncated { get; set; }
    /// Number of files whose hunk content was omitted because the limit was reached.
    public int TruncatedFileCount { get; set; }

    public int FileCount => Files.Count;
    public int TotalAdditions => Files.Sum(f => f.Additions);
    public int TotalDeletions => Files.Sum(f => f.Deletions);
}

public class DiffFile
{
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public DiffFileStatus Status { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public List<DiffHunk> Hunks { get; set; } = [];
    public bool IsBinary { get; set; }
    /// True when this file's hunk content was omitted because the total line limit was reached.
    public bool IsTruncated { get; set; }

    public string DisplayPath => Status == DiffFileStatus.Renamed
        ? $"{OldPath} → {NewPath}"
        : NewPath.Length > 0 ? NewPath : OldPath;

    public string FileName => System.IO.Path.GetFileName(
        NewPath.Length > 0 ? NewPath : OldPath);

    public string FileDirectory
    {
        get
        {
            var path = NewPath.Length > 0 ? NewPath : OldPath;
            var dir = System.IO.Path.GetDirectoryName(path.Replace('/', '\\')) ?? string.Empty;
            return dir.Length > 0 ? dir.Replace('\\', '/') + "/" : string.Empty;
        }
    }
}

public class DiffHunk
{
    public int OldStart { get; set; }
    public int OldCount { get; set; }
    public int NewStart { get; set; }
    public int NewCount { get; set; }
    public string Context { get; set; } = string.Empty;
    public List<DiffLine> Lines { get; set; } = [];
}

public class DiffLine
{
    public DiffLineType Type { get; set; }
    public string Content { get; set; } = string.Empty;
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
}

public enum DiffFileStatus { Modified, Added, Deleted, Renamed, Copied }
public enum DiffLineType { Context, Addition, Deletion }
