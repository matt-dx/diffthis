namespace DiffThis.Models;

public class AiCacheEntry
{
    public string   Response { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public string   Model    { get; set; } = string.Empty;
}
