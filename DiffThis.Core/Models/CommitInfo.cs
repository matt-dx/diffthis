namespace DiffThis.Core.Models;

public class CommitInfo
{
    public string Hash        { get; set; } = string.Empty;
    public string ShortHash   { get; set; } = string.Empty;
    public string Subject     { get; set; } = string.Empty;
    public string Author      { get; set; } = string.Empty;
    public string RelativeDate { get; set; } = string.Empty;

    public string Display
    {
        get
        {
            var subj = Subject.Length > 72 ? Subject[..72] + "…" : Subject;
            return $"{ShortHash}  {subj}  ({Author}, {RelativeDate})";
        }
    }
}
