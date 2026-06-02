using System.Collections.Concurrent;
using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

/// Identifies a unique AI result: which diff, which feature, which run config.
public record AiRunKey(string Feature, string Model, bool ToolsEnabled, int MaxTurns)
{
    /// Short label used on model tabs, e.g. "Sonnet 4.6 · tools · 5t"
    public string TabLabel(string modelDisplayName)
    {
        var cfg = ToolsEnabled
            ? MaxTurns > 0 ? $"tools · {MaxTurns}t" : "tools"
            : MaxTurns > 0 ? $"{MaxTurns}t"          : null;
        return cfg is null ? modelDisplayName : $"{modelDisplayName} · {cfg}";
    }
}

public class AiCacheService
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffThis", "ai-cache.json");

    private const int MaxEntries  = 500;
    private const int TrimTarget  = 400;

    // ConcurrentDictionary is safe for concurrent Get/Set from multiple async AI completions
    private ConcurrentDictionary<string, AiCacheEntry> _cache = new();

    public AiCacheService() => Load();

    // ── Public API ────────────────────────────────────────────────────────

    public AiCacheEntry? Get(string repoPath, string baseRef, string compareRef, AiRunKey key)
    {
        _cache.TryGetValue(CacheKey(repoPath, baseRef, compareRef, key), out var e);
        return e;
    }

    public void Set(string repoPath, string baseRef, string compareRef, AiRunKey runKey, string response)
    {
        _cache[CacheKey(repoPath, baseRef, compareRef, runKey)] = new AiCacheEntry
        {
            Response     = response,
            CachedAt     = DateTime.UtcNow,
            Model        = runKey.Model,
            ToolsEnabled = runKey.ToolsEnabled,
            MaxTurns     = runKey.MaxTurns,
        };
        Save();
    }

    public int Count => _cache.Count;

    public void Clear()
    {
        _cache.Clear();
        Save();
    }

    public void Remove(string repoPath, string baseRef, string compareRef, AiRunKey runKey)
    {
        if (_cache.TryRemove(CacheKey(repoPath, baseRef, compareRef, runKey), out _))
            Save();
    }

    /// All cached entries for a diff, keyed by AiRunKey.
    public Dictionary<AiRunKey, AiCacheEntry> GetAll(string repoPath, string baseRef, string compareRef)
    {
        var prefix = $"{Esc(repoPath)}|{Esc(baseRef)}|{Esc(compareRef)}|";
        return _cache
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                kv => ParseRunKey(kv.Key),
                kv => kv.Value);
    }

    // ── Key encoding ──────────────────────────────────────────────────────
    // Format: {esc(repo)}|{esc(base)}|{esc(compare)}|{esc(feature)}|{esc(model)}|{T|N}{maxTurns}

    private static string CacheKey(string repo, string baseRef, string compare, AiRunKey k)
        => $"{Esc(repo)}|{Esc(baseRef)}|{Esc(compare)}|{Esc(k.Feature)}|{Esc(k.Model)}|{(k.ToolsEnabled ? 'T' : 'N')}{k.MaxTurns}";

    private static AiRunKey ParseRunKey(string key)
    {
        // Work backwards: last segment = config, second-to-last = model,
        // third-to-last = feature (first three segments are repo/base/compare)
        var parts = key.Split('|');
        if (parts.Length < 6) return new AiRunKey("", key, false, 0);

        var cfg     = parts[^1];                      // e.g. "T5" or "N0"
        var model   = parts[^2];
        var feature = parts[^3];
        var tools   = cfg.Length > 0 && cfg[0] == 'T';
        var turns   = int.TryParse(cfg.Length > 1 ? cfg[1..] : "0", out var t) ? t : 0;
        return new AiRunKey(feature, model, tools, turns);
    }

    private static string Esc(string s) => s.Replace("|", "%7C");

    // ── Persistence ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, AiCacheEntry>>(json);
            if (dict is not null)
                _cache = new ConcurrentDictionary<string, AiCacheEntry>(dict);
        }
        catch { _cache = new(); }
    }

    private void Save()
    {
        try
        {
            lock (_cache)
            {
                // Evict oldest entries if the cache has grown too large
                if (_cache.Count > MaxEntries)
                {
                    var toRemove = _cache
                        .OrderBy(kv => kv.Value.CachedAt)
                        .Take(_cache.Count - TrimTarget)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var k in toRemove)
                        _cache.TryRemove(k, out _);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

                var tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(
                    new Dictionary<string, AiCacheEntry>(_cache),
                    new JsonSerializerOptions { WriteIndented = false }));
                File.Move(tmp, CachePath, overwrite: true);
            }
        }
        catch { /* best effort */ }
    }
}
