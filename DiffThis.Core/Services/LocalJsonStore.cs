using System.Text.Json;

namespace DiffThis.Core.Services;

/// File-backed key/value store for state that must reliably survive app restarts.
/// MAUI's <c>Preferences</c> is backed by Windows ApplicationData, which ties
/// persistence to the app's package identity — fragile for this app's unpackaged
/// Debug builds. AI model catalogs are cached here instead, the same way
/// AiCacheService and LogService already persist directly to a file under
/// %LOCALAPPDATA%\DiffThis rather than through Preferences.
public static class LocalJsonStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffThis", "model-cache.json");

    private static readonly object Lock = new();
    private static Dictionary<string, string>? _values;

    public static string Get(string key, string defaultValue)
    {
        lock (Lock)
        {
            EnsureLoaded();
            if (_values!.TryGetValue(key, out var value)) return value;

            // Before this store existed, the three AI model services kept their catalogs (models,
            // last-fetched timestamp, and per-provider extras like custom names/hidden flags/
            // reasoning settings) in Preferences under these same key names. Migrate a value found
            // there on first read so upgrading doesn't silently drop it back to defaults.
            try
            {
                if (Preferences.ContainsKey(key))
                {
                    var legacy = Preferences.Get(key, defaultValue);
                    _values[key] = legacy;
                    Persist();
                    return legacy;
                }
            }
            catch { /* legacy storage unavailable — fall through to the default */ }

            return defaultValue;
        }
    }

    public static void Set(string key, string value)
    {
        lock (Lock)
        {
            EnsureLoaded();
            _values![key] = value;
            Persist();
        }
    }

    private static void EnsureLoaded()
    {
        if (_values is not null) return;
        try
        {
            _values = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch { _values = []; }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_values));
        }
        catch { /* best effort */ }
    }
}
