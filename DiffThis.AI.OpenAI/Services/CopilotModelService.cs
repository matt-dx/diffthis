using System.Text.Json;
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
                        string? capType = null;
                        if (e.TryGetProperty("capabilities", out var caps)
                            && caps.TryGetProperty("type", out var typeEl))
                            capType = typeEl.GetString();

                        return (id: cleanId, display: friendlyName ?? name ?? InferName(cleanId), capType);
                    })
                    .Where(x => !string.IsNullOrEmpty(x.id)
                                && IsChatModel(x.id)
                                && IsChatCapability(x.capType))
                    .Select(x => (x.id, x.display))
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

    private void MergeInto(List<(string id, string display)> fetched, bool fromApi)
    {
        var merged = new List<CopilotModel>();
        foreach (var (id, display) in fetched)
        {
            var existing = _models.FirstOrDefault(m => m.Id == id);
            merged.Add(new CopilotModel
            {
                Id           = id,
                DisplayName  = existing?.IsCustomName == true ? existing.DisplayName : display,
                IsCustomName = existing?.IsCustomName ?? false,
                // All GitHub Models share the same ~8k token per-request cap, so
                // there is no meaningful reason to hide "small context" models —
                // the 28 000-char diff limit applies equally to every model.
                // New models: visible by default; existing models: preserve user choice.
                IsHidden     = existing?.IsHidden ?? false,
            });
        }
        _models         = merged;
        LastFetchedAt   = DateTime.UtcNow;
        IsUsingDefaults = !fromApi;
        Save();
    }

    private void ApplyDefaults()
    {
        var list = DefaultModels.Select(x => (x.Id, x.Name)).ToList();
        MergeInto(list, fromApi: false);
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
            && !lower.Contains("codex");   // completion models, not chat
    }

    // ── Name inference ────────────────────────────────────────────────────
    // Model IDs are "Publisher/model-name" or bare "model-name".
    // Strip the publisher prefix, then apply known mappings.

    internal static string InferName(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var slash = id.LastIndexOf('/');
        var slug  = slash >= 0 ? id[(slash + 1)..] : id;

        return slug.ToLowerInvariant() switch
        {
            "gpt-4o"                         => "GPT-4o",
            "gpt-4o-mini"                    => "GPT-4o Mini",
            "o1"                             => "o1",
            "o1-mini"                        => "o1 Mini",
            "o3-mini"                        => "o3 Mini",
            "o3"                             => "o3",
            "phi-4"                          => "Phi 4",
            "mistral-nemo"                   => "Mistral Nemo",
            _ => TitleCaseSlug(slug),
        };
    }

    private static string TitleCaseSlug(string slug)
    {
        // "Llama-3.3-70B-Instruct" → "Llama 3.3 70B Instruct"
        // Strip trailing date suffix (8 digits): "command-r-plus-08-2024" → "command-r-plus"
        var parts = slug.Split('-');
        // Drop trailing date parts (pure 2-4 digit numbers at end, e.g. "2024", "08")
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
