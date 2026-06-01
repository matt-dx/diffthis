using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffThis.Models;

namespace DiffThis.Services;

public partial class ClaudeModelService : IClaudeModelService
{
    // ── Preferences keys ──────────────────────────────────────────────────
    private const string ModelsKey    = "claude_models";
    private const string FetchedAtKey = "claude_models_fetched_at";

    // ── Tier sort order ───────────────────────────────────────────────────
    private static readonly string[] TierOrder = ["opus", "sonnet", "haiku"];

    private readonly IClaudeAuthService _auth;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.anthropic.com") };

    private List<ClaudeModel> _models = [];

    public IReadOnlyList<ClaudeModel> Models       => _models;
    public DateTime?                  LastFetchedAt { get; private set; }
    public bool                       IsLoading    { get; private set; }

    public event Action? ModelsChanged;

    public ClaudeModelService(IClaudeAuthService auth)
    {
        _auth = auth;
        Load();

        // Auto-fetch in background on first run
        if (_models.Count == 0)
            _ = RefreshAsync();
    }

    // ── Public API ────────────────────────────────────────────────────────

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        if (_auth.State != ClaudeAuthState.Authenticated) return;

        IsLoading = true;
        ModelsChanged?.Invoke();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models?limit=100");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.AccessToken);
            req.Headers.Add("anthropic-version", "2023-06-01");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return;

            var fetched = data.EnumerateArray()
                .Select(e => e.GetProperty("id").GetString() ?? "")
                .Where(id => id.StartsWith("claude-", StringComparison.Ordinal))
                .ToList();

            // Merge: keep custom names; add new entries; remove gone entries
            var merged = new List<ClaudeModel>();
            foreach (var id in fetched)
            {
                var existing = _models.FirstOrDefault(m => m.Id == id);
                merged.Add(new ClaudeModel
                {
                    Id           = id,
                    DisplayName  = existing?.IsCustomName == true ? existing.DisplayName : InferName(id),
                    IsCustomName = existing?.IsCustomName ?? false,
                });
            }

            // Sort: by tier order, then by descending version
            _models = merged.OrderBy(TierRank).ThenByDescending(VersionKey).ToList();
            LastFetchedAt = DateTime.UtcNow;
            Save();
        }
        catch { /* network error — keep existing list */ }
        finally
        {
            IsLoading = false;
            ModelsChanged?.Invoke();
        }
    }

    public void SetDisplayName(string modelId, string name)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.DisplayName  = name.Trim();
        m.IsCustomName = true;
        Save();
        ModelsChanged?.Invoke();
    }

    public void ResetDisplayName(string modelId)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.DisplayName  = InferName(modelId);
        m.IsCustomName = false;
        Save();
        ModelsChanged?.Invoke();
    }

    public string GetDisplayName(string modelId)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        return m?.DisplayName ?? InferName(modelId);
    }

    // ── Name inference ────────────────────────────────────────────────────
    // Pattern: claude-{tier}-{major}[-{minor_or_date}][-{date}]
    // A segment of exactly 8 digits is treated as a date suffix, not a version component.

    [GeneratedRegex(@"^claude-(\w+)-(\d+)(?:-(\d+))?(?:-(\d{8}))?$")]
    private static partial Regex ModelIdRegex();

    internal static string InferName(string id)
    {
        var m = ModelIdRegex().Match(id);
        if (!m.Success) return id;

        var tier  = Capitalize(m.Groups[1].Value);
        var major = m.Groups[2].Value;
        var g3    = m.Groups[3].Success ? m.Groups[3].Value : null;

        // g3 is a date (8 digits) → no minor version; g3 is short → minor version
        string? minor = g3?.Length == 8 ? null : g3;

        return minor is null ? $"{tier} {major}" : $"{tier} {major}.{minor}";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // ── Sorting helpers ───────────────────────────────────────────────────

    private static int TierRank(ClaudeModel m)
    {
        var match = ModelIdRegex().Match(m.Id);
        if (!match.Success) return TierOrder.Length;
        var tier = match.Groups[1].Value;
        var idx  = Array.IndexOf(TierOrder, tier);
        return idx < 0 ? TierOrder.Length : idx;
    }

    private static string VersionKey(ClaudeModel m)
    {
        // Returns a string that sorts lexicographically descending (newest first).
        // Pad major and minor with zeros so "4.8" > "4.1" > "4".
        var match = ModelIdRegex().Match(m.Id);
        if (!match.Success) return "0000.0000";
        var major = match.Groups[2].Value.PadLeft(4, '0');
        var g3    = match.Groups[3].Success ? match.Groups[3].Value : null;
        var minor = g3?.Length == 8 ? "0000" : (g3?.PadLeft(4, '0') ?? "0000");
        return $"{major}.{minor}";
    }

    // ── Persistence ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            var json = Preferences.Get(ModelsKey, "[]");
            _models = JsonSerializer.Deserialize<List<ClaudeModel>>(json) ?? [];
            var ts = Preferences.Get(FetchedAtKey, "");
            LastFetchedAt = string.IsNullOrEmpty(ts) ? null : DateTime.Parse(ts).ToUniversalTime();
        }
        catch { _models = []; }
    }

    private void Save()
    {
        try
        {
            Preferences.Set(ModelsKey, JsonSerializer.Serialize(_models));
            Preferences.Set(FetchedAtKey, LastFetchedAt?.ToString("O") ?? "");
        }
        catch { /* best effort */ }
    }
}
