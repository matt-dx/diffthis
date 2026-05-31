using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

public class AiCacheService
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffThis", "ai-cache.json");

    private Dictionary<string, AiCacheEntry> _cache = [];

    public AiCacheService() => Load();

    // ── Public API ────────────────────────────────────────────────────────

    public AiCacheEntry? Get(string repoPath, string baseRef, string compareRef,
                             string feature, string model)
    {
        _cache.TryGetValue(Key(repoPath, baseRef, compareRef, feature, model), out var e);
        return e;
    }

    public void Set(string repoPath, string baseRef, string compareRef,
                    string feature, string model, string response)
    {
        _cache[Key(repoPath, baseRef, compareRef, feature, model)] =
            new AiCacheEntry { Response = response, CachedAt = DateTime.UtcNow, Model = model };
        Save();
    }

    public void Remove(string repoPath, string baseRef, string compareRef,
                       string feature, string model)
    {
        if (_cache.Remove(Key(repoPath, baseRef, compareRef, feature, model)))
            Save();
    }

    /// Returns all cached entries for a given diff, keyed by (feature, model).
    public Dictionary<(string feature, string model), AiCacheEntry> GetAll(
        string repoPath, string baseRef, string compareRef)
    {
        var prefix = $"{Esc(repoPath)}|{Esc(baseRef)}|{Esc(compareRef)}|";
        return _cache
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                kv => SplitFeatureModel(kv.Key),
                kv => kv.Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Key(string repo, string baseRef, string compare,
                               string feature, string model)
        => $"{Esc(repo)}|{Esc(baseRef)}|{Esc(compare)}|{feature}|{model}";

    private static (string feature, string model) SplitFeatureModel(string key)
    {
        // key = {repo}|{base}|{compare}|{feature}|{model}
        // Split on the LAST two pipe-separated segments (first three may contain escaped pipes)
        var idx = key.LastIndexOf('|');
        if (idx < 0) return ("", key);
        var model = key[(idx + 1)..];
        var idx2  = key.LastIndexOf('|', idx - 1);
        var feature = idx2 < 0 ? key[..idx] : key[(idx2 + 1)..idx];
        return (feature, model);
    }

    private static string Esc(string s) => s.Replace("|", "%7C");

    private void Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, AiCacheEntry>>(json) ?? [];
        }
        catch { _cache = []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache,
                new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { /* best effort */ }
    }
}
