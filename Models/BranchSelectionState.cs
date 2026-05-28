namespace DiffThis.Models;

public class BranchSelectionState
{
    public string BaseBranch    { get; set; } = string.Empty;
    public string CompareBranch { get; set; } = string.Empty;
    public bool   PinBase       { get; set; }
    public bool   PinCompare    { get; set; }
    public string BaseCommit    { get; set; } = string.Empty;
    public string CompareCommit { get; set; } = string.Empty;
}
