using System.Text.Json;
using System.Text.RegularExpressions;
using DiffThis.AI.OpenAI.Models;

namespace DiffThis.AI.OpenAI.Services;

public class CopilotModelService : ICopilotModelService
{
    private const string ModelsKey       = "ghmodels_models";
    private const string FetchedAtKey    = "ghmodels_models_fetched_at";
    private const string SourceKey       = "ghmodels_models_source";

    // Curated fallback — used when the API fetch fails or on first run.
    // These are the model IDs used by api.githubcopilot.com (bare IDs, not
    // Publisher/model-name format used by the old Azure AI Inference catalog).
    private static readonly (string Id, string Name)[] DefaultModels =
    [
        ("gpt-4o",                "GPT-4o"),
        ("gpt-4o-mini",           "GPT-4o Mini"),
        ("o1",                    "o1"),
        ("o1-mini",               "o1 Mini"),
        ("o3-mini",               "o3 Mini"),
        ("claude-3.5-sonnet",     "Claude 3.5 Sonnet"),
        ("claude-3.7-sonnet",     "Claude 3.7 Sonnet"),
        ("gemini-2.0-flash",      "Gemini 2.0 Flash"),
        ("gemini-1.5-pro",        "Gemini 1.5 Pro"),
        ("mistral-large",         "Mistral Large"),
        ("cohere-command-r-plus", "Command R+"),
    ];

    private readonly ICopilotAuthService _auth;
    private readonly HttpClient          _http = new();

    private List<CopilotModel> _models = [];

    public IReadOnlyList<CopilotModel> Models        => _models;
    public IReadOnlyList<CopilotModel> VisibleModels => _models.Where(m => !m.IsHidden).ToList();
    public bool                       IsLoading       { get; private set; }
    public DateTime?                  LastFetchedAt   { get; private set; }
    public bool                       IsUsingDefaults { get; private set; } = true;

    public event Action? ModelsChanged;

    public CopilotModelService(ICopilotAuthService auth)
    {
        _auth = auth;
        Load();

        if (_models.Count == 0)
            _ = RefreshAsync();
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;

        IsLoading = true;
        ModelsChanged?.Invoke();

        try
        {
            var token = await _auth.GetSessionTokenAsync(ct);
            if (token is null)
            {
                // Auth not available — populate from defaults so the UI isn't empty
                if (_models.Count == 0)
                    ApplyDefaults();
                return;
            }

            // GitHub Copilot models catalog
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.githubcopilot.com/models");
            req.Headers.Add("Authorization",          $"Bearer {token}");
            req.Headers.Add("Copilot-Integration-Id", "vscode-chat");
            req.Headers.Add("User-Agent",             "DiffThis/1.0");

            var resp = await _http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                // Response may be an array or { "data": [...] }
                var root  = doc.RootElement;
                JsonElement array;
                if (root.ValueKind == JsonValueKind.Array)
                    array = root;
                else if (root.TryGetProperty("data", out var data))
                    array = data;
                else
                {
                    ApplyDefaults();
                    return;
                }

                var fetched = array.EnumerateArray()
                    .Select(e =>
                    {
                        var rawId        = e.TryGetProperty("id",            out var i) ? i.GetString() ?? "" : "";
                        var friendlyName = e.TryGetProperty("friendly_name", out var f) ? f.GetString() : null;
                        var name         = e.TryGetProperty("name",          out var n) ? n.GetString() : null;
                        var cleanId      = ExtractModelId(rawId);

                        // capabilities.type identifies whether the model supports chat.
                        // Only "chat" models can be used with /chat/completions.
                        string?  capType          = null;
                        string[] reasoningEfforts = [];
                        if (e.TryGetProperty("capabilities", out var caps))
                        {
                            if (caps.TryGetProperty("type", out var typeEl))
                                capType = typeEl.GetString();
                            // capabilities.supports.reasoning_effort: ["low","medium","high"] (thinking models only)
                            if (caps.TryGetProperty("supports", out var sup)
                                && sup.TryGetProperty("reasoning_effort", out var re)
                                && re.ValueKind == JsonValueKind.Array)
                            {
                                reasoningEfforts = re.EnumerateArray()
                                    .Select(x => x.GetString() ?? "")
                                    .Where(x => x.Length > 0)
                                    .ToArray();
                            }
                        }

                        return (id: cleanId, display: friendlyName ?? name ?? InferName(cleanId), capType, reasoningEfforts);
                    })
                    .Where(x => !string.IsNullOrEmpty(x.id)
                                && IsChatModel(x.id)
                                && IsChatCapability(x.capType))
                    .Select(x => (x.id, x.display, x.reasoningEfforts))
                    .ToList();

                if (fetched.Count > 0)
                {
                    MergeInto(fetched, fromApi: true);
                    return;
                }
            }

            // API unavailable or returned nothing — use defaults
            if (_models.Count == 0)
                ApplyDefaults();
        }
        catch
        {
            if (_models.Count == 0)
                ApplyDefaults();
        }
        finally
        {
            IsLoading = false;
            ModelsChanged?.Invoke();
        }
    }

    private void MergeInto(List<(string id, string display, string[] reasoningEfforts)> fetched, bool fromApi)
    {
        // Step 1: build initial display names, detect preview/dated models
        var parsed = fetched.Select(f =>
        {
            var (baseId, dateSuffix, isPreview) = ParseModelId(f.id);
            return (f.id, baseId, dateSuffix, isPreview, rawDisplay: f.display, f.reasoningEfforts);
        }).ToList();

        // Step 2: for each model, determine the canonical representative of its base group.
        // Canonical priority: undated + non-preview > dated (newest date first) > preview.
        var groups = parsed.GroupBy(p => p.baseId).ToDictionary(g => g.Key, g => g.ToList());
        var canonicalIds = new HashSet<string>();
        foreach (var group in groups.Values)
        {
            // Prefer the plain undated non-preview member
            var plain = group.FirstOrDefault(p => p.dateSuffix is null && !p.isPreview);
            if (plain != default) { canonicalIds.Add(plain.id); continue; }
            // Otherwise, newest dated version
            var newest = group.Where(p => p.dateSuffix is not null && !p.isPreview)
                              .OrderByDescending(p => p.dateSuffix)
                              .FirstOrDefault();
            if (newest != default) { canonicalIds.Add(newest.id); continue; }
            // Fall back to preview if nothing else
            canonicalIds.Add(group[0].id);
        }

        // Step 3: assign display names, ensuring uniqueness
        var nameCount   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>();
        foreach (var p in parsed)
        {
            var name = BuildDisplayName(p.id, p.baseId, p.dateSuffix, p.isPreview);
            nameCount[name] = (nameCount.TryGetValue(name, out var c) ? c : 0) + 1;
            displayNames[p.id] = name;
        }
        // Disambiguate: for names that appear more than once, append the suffix back
        foreach (var p in parsed.Where(p => nameCount.TryGetValue(displayNames[p.id], out var n) && n > 1))
        {
            var suffix = p.isPreview  ? " (Preview)"
                       : p.dateSuffix is not null ? $" ({FormatDate(p.dateSuffix)})"
                       : "";
            if (suffix.Length > 0)
                displayNames[p.id] = BuildBaseDisplayName(p.baseId) + suffix;
        }

        // Step 4: merge with existing model list
        var merged = new List<CopilotModel>();
        foreach (var p in parsed)
        {
            var existing    = _models.FirstOrDefault(m => m.Id == p.id);
            var isCanonical = canonicalIds.Contains(p.id);
            merged.Add(new CopilotModel
            {
                Id               = p.id,
                DisplayName      = existing?.IsCustomName == true ? existing.DisplayName : displayNames[p.id],
                IsCustomName     = existing?.IsCustomName ?? false,
                // New models: canonical=visible, non-canonical (dated/preview duplicates)=hidden.
                // Existing models: preserve user choice.
                IsHidden         = existing is not null ? existing.IsHidden : !isCanonical,
                // From API: supported effort levels. Preserve user's choice across refreshes.
                ReasoningEfforts = p.reasoningEfforts,
                ReasoningEffort  = existing?.ReasoningEffort,
            });
        }
        _models         = merged;
        LastFetchedAt   = DateTime.UtcNow;
        IsUsingDefaults = !fromApi;
        Save();
    }

    // Parse "gpt-4o-2024-11-20" → ("gpt-4o", "2024-11-20", false)
    // Parse "gemini-3-flash-preview" → ("gemini-3-flash", null, true)
    // Parse "gpt-3.5-turbo-0613"    → ("gpt-3.5-turbo",  "0613", false)
    internal static (string baseId, string? dateSuffix, bool isPreview) ParseModelId(string id)
    {
        var isPreview = id.EndsWith("-preview", StringComparison.OrdinalIgnoreCase);
        var working   = isPreview ? id[..^"-preview".Length] : id;

        // Full ISO date: -YYYY-MM-DD
        var fullDate = Regex.Match(working, @"-(\d{4}-\d{2}-\d{2})$");
        if (fullDate.Success)
            return (working[..^(fullDate.Groups[1].Value.Length + 1)], fullDate.Groups[1].Value, isPreview);

        // 4-digit version snapshot: -MMDD or -NNNN (e.g. gpt-4-0613)
        var shortVer = Regex.Match(working, @"-(\d{4})$");
        if (shortVer.Success)
            return (working[..^5], shortVer.Groups[1].Value, isPreview);

        return (working, null, isPreview);
    }

    private static string BuildDisplayName(string id, string baseId, string? dateSuffix, bool isPreview)
    {
        var baseName = BuildBaseDisplayName(baseId);
        if (isPreview) return $"{baseName} (Preview)";
        return baseName;
    }

    private static string BuildBaseDisplayName(string baseId)
    {
        // Strip publisher prefix
        var slash = baseId.LastIndexOf('/');
        var slug  = slash >= 0 ? baseId[(slash + 1)..] : baseId;

        return slug.ToLowerInvariant() switch
        {
            "gpt-4o"      => "GPT-4o",
            "gpt-4o-mini" => "GPT-4o Mini",
            "gpt-4"       => "GPT-4",
            "gpt-4-turbo" => "GPT-4 Turbo",
            "gpt-3.5-turbo" or "gpt-3.5" => "GPT-3.5 Turbo",
            "o1"          => "o1",
            "o1-mini"     => "o1 Mini",
            "o3"          => "o3",
            "o3-mini"     => "o3 Mini",
            "o4-mini"     => "o4 Mini",
            "phi-4"       => "Phi 4",
            "mistral-nemo" => "Mistral Nemo",
            _ => TitleCaseSlug(slug),
        };
    }

    private static string FormatDate(string dateSuffix)
    {
        // "2024-11-20" → "Nov 2024"; "0613" → "0613" (keep as-is for short codes)
        if (DateTime.TryParse(dateSuffix, out var dt))
            return dt.ToString("MMM yyyy");
        return dateSuffix;
    }

    private void ApplyDefaults()
    {
        var list = DefaultModels.Select(x => (x.Id, x.Name, Array.Empty<string>())).ToList();
        MergeInto(list, fromApi: false);
    }

    public string? GetReasoningEffort(string modelId)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        if (m is not null && m.ReasoningEfforts.Length > 0)
            return m.ReasoningEffort ?? "low";
        // Fallback for built-in defaults list: Claude Opus 4.x is a known thinking model
        if (modelId.StartsWith("claude-opus-4", StringComparison.OrdinalIgnoreCase))
            return m?.ReasoningEffort ?? "low";
        return null;
    }

    public void SetReasoningEffort(string modelId, string? effort)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.ReasoningEffort = effort;
        Save();
        ModelsChanged?.Invoke();
    }

    // ── Model management ──────────────────────────────────────────────────

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

    public void ToggleHidden(string modelId)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.IsHidden = !m.IsHidden;
        Save();
        ModelsChanged?.Invoke();
    }

    public string GetDisplayName(string modelId)
    {
        var m = _models.FirstOrDefault(x => x.Id == modelId);
        return m?.DisplayName ?? InferName(modelId);
    }

    // ── Model ID extraction ───────────────────────────────────────────────

    // Azure ML registry URIs: "azureml://registries/azure-openai/models/gpt-4o/versions/2"
    // → extract just the model name: "gpt-4o"
    private static string ExtractModelId(string rawId)
    {
        const string marker = "/models/";
        var start = rawId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return rawId;
        start += marker.Length;
        var end = rawId.IndexOf('/', start);
        return end < 0 ? rawId[start..] : rawId[start..end];
    }

    // Primary filter: capabilities.type from the API response.
    // Absent = field not returned by this endpoint version; treat as allowed so
    // we don't silently drop every model if GitHub changes the schema.
    private static bool IsChatCapability(string? capType) =>
        capType is null or "chat";

    // Secondary name-based filter: drop models that are clearly not chat models
    // even when the API doesn't return capability metadata.
    private static bool IsChatModel(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        return !lower.Contains("embed")
            && !lower.Contains("text-embedding")
            && !lower.Contains("whisper")
            && !lower.Contains("tts")
            && !lower.Contains("dall-e")
            && !lower.Contains("davinci")
            && !lower.Contains("babbage")
            && !lower.Contains("ada")
            && !lower.Contains("codex")     // completion models, not chat
            && !lower.Contains("trajectory-compaction"); // internal Copilot context-compression model
    }

    // ── Name inference ────────────────────────────────────────────────────
    // Used by GetDisplayName (fallback) and ResetDisplayName.

    internal static string InferName(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var (baseId, _, isPreview) = ParseModelId(id);
        var name = BuildBaseDisplayName(baseId);
        return isPreview ? $"{name} (Preview)" : name;
    }

    private static string TitleCaseSlug(string slug)
    {
        // "Llama-3.3-70B-Instruct" → "Llama 3.3 70B Instruct"
        var parts = slug.Split('-');
        // Drop trailing digit-only parts that are clearly version stamps
        var end = parts.Length;
        while (end > 1 && parts[end - 1].All(char.IsDigit) && parts[end - 1].Length <= 4)
            end--;
        return string.Join(" ", parts[..end]
            .Select(p => p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : p));
    }

    // ── Persistence ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            var json = Preferences.Get(ModelsKey, "[]");
            _models = JsonSerializer.Deserialize<List<CopilotModel>>(json) ?? [];
            var ts  = Preferences.Get(FetchedAtKey, "");
            LastFetchedAt   = string.IsNullOrEmpty(ts) ? null : DateTime.Parse(ts).ToUniversalTime();
            IsUsingDefaults = Preferences.Get(SourceKey, "defaults") == "defaults";
        }
        catch { _models = []; }
    }

    private void Save()
    {
        try
        {
            Preferences.Set(ModelsKey,    JsonSerializer.Serialize(_models));
            Preferences.Set(FetchedAtKey, LastFetchedAt?.ToString("O") ?? "");
            Preferences.Set(SourceKey,    IsUsingDefaults ? "defaults" : "api");
        }
        catch { /* best effort */ }
    }
}
