namespace DiffThis.Models;

public class ClaudeModel
{
    public string Id           { get; set; } = string.Empty;  // e.g. "claude-opus-4-8"
    public string DisplayName  { get; set; } = string.Empty;  // e.g. "Opus 4.8"
    public bool   IsCustomName { get; set; }                  // true when user has overridden the name
}
