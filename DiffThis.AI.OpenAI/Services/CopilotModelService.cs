using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

public class CopilotModelService : ICopilotModelService
{
    private const string ModelsKey       = "ghmodels_models";
    private const string FetchedAtKey    = "ghmodels_models_fetched_at";
    private const string SourceKey       = "ghmodels_models_source";

    // Curated fallback — used when the API fetch fails or on first run.
    // Model IDs match the GitHub Models catalog (Publisher/model-name format).
    private static readonly (string Id, string Name)[] DefaultModels =
    [
        ("openai/gpt-4o",                       "GPT-4o"),
        ("openai/gpt-4o-mini",                  "GPT-4o Mini"),
        ("openai/o1",                           "o1"),
        ("openai/o1-mini",                      "o1 Mini"),
        ("Anthropic/claude-3-5-sonnet",         "Claude 3.5 Sonnet"),
        ("Anthropic/claude-3-5-haiku",          "Claude 3.5 Haiku"),
        ("meta/Llama-3.3-70B-Instruct",         "Llama 3.3 70B"),
        ("meta/Llama-3.1-405B-Instruct",        "Llama 3.1 405B"),
        ("microsoft/Phi-4",                     "Phi 4"),
        ("mistral-ai/Mistral-Large-2411",       "Mistral Large"),
        ("mistral-ai/Mistral-Nemo",             "Mistral Nemo"),
        ("Cohere/command-r-plus-08-2024",       "Command R+"),
    ];

    private readonly ICopilotAuthService _auth;
    private readonly HttpClient          _http = new();

    private List<ClaudeModel> _models = [];

    public IReadOnlyList<ClaudeModel> Models        => _models;
    public IReadOnlyList<ClaudeModel> VisibleModels => _models.Where(m => !m.IsHidden).ToList();
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

            // GitHub Models catalog endpoint (Azure AI Inference format)
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://models.inference.ai.azure.com/models");
            req.Headers.Add("Authorization", $"Bearer {token}");

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
                        var rawId        = e.TryGetProperty("id",           out var i) ? i.GetString() ?? "" : "";
                        var friendlyName = e.TryGetProperty("friendly_name", out var f) ? f.GetString() : null;
                        var name         = e.TryGetProperty("name",          out var n) ? n.GetString() : null;
                        var cleanId      = ExtractModelId(rawId);
                        return (id: cleanId, display: friendlyName ?? name ?? InferName(cleanId));
                    })
                    .Where(x => !string.IsNullOrEmpty(x.id) && IsChatModel(x.id))
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
        var merged = new List<ClaudeModel>();
        foreach (var (id, display) in fetched)
        {
            var existing = _models.FirstOrDefault(m => m.Id == id);
            merged.Add(new ClaudeModel
            {
                Id           = id,
                DisplayName  = existing?.IsCustomName == true ? existing.DisplayName : display,
                IsCustomName = existing?.IsCustomName ?? false,
                // New models: hide by default unless they have a large context window.
                // Existing models: preserve whatever the user chose.
                IsHidden     = existing?.IsHidden ?? !IsLargeContextModel(id),
            });
        }
        _models         = merged;
        LastFetchedAt   = DateTime.UtcNow;
        IsUsingDefaults = !fromApi;
        Save();
    }

    // Models known to have a large enough context window (≥32k tokens) to handle full diffs.
    // Everything else (Llama, Phi, Mistral, Cohere, etc.) is hidden by default — the user can
    // unhide them in Settings for small diffs.
    private static bool IsLargeContextModel(string modelId)
    {
        var s = modelId.ToLowerInvariant();
        return s.StartsWith("gpt-4o")
            || s.StartsWith("o1")
            || s.StartsWith("o3")
            || s.Contains("claude-3-5")
            || s.Contains("claude-3-7")
            || s.Contains("claude-3-opus")
            || s.Contains("gpt-4-turbo")
            || s.Contains("command-r");   // Cohere Command R has 128k context
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

    public int GetEffectiveDiffCharLimit(string modelId) =>
        IsLargeContextModel(modelId) ? 60_000 : 24_000;

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

    // Keep only chat/generation models — drop embeddings, TTS, image gen, etc.
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
            && !lower.Contains("ada");
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
            _models = JsonSerializer.Deserialize<List<ClaudeModel>>(json) ?? [];
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
