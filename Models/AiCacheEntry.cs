namespace DiffThis.Models;

public class AiCacheEntry
{
    public string   Response     { get; set; } = string.Empty;
    public DateTime CachedAt     { get; set; }
    public string   Model        { get; set; } = string.Empty;
    public bool     ToolsEnabled { get; set; }
    public int      MaxTurns     { get; set; }  // 0 = unlimited

    /// Short label shown on model tabs, e.g. "tools · 5t" or "3t"
    public string ConfigLabel =>
        ToolsEnabled
            ? MaxTurns > 0 ? $"tools · {MaxTurns}t" : "tools"
            : MaxTurns > 0 ? $"{MaxTurns}t"          : "";
}
