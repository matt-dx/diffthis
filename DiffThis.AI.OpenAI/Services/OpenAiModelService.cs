using System.Diagnostics;
using System.Text.Json;
using DiffThis.AI.OpenAI.Models;

namespace DiffThis.AI.OpenAI.Services;

public class OpenAiModelService : IOpenAiModelService
{
    private const string ModelsKey = "openai_codex_models";
    private const string FetchedAtKey = "openai_codex_models_fetched_at";
    private readonly OpenAiAuthService _auth;
    private List<OpenAiModel> _models = [];

    public IReadOnlyList<OpenAiModel> Models => _models;
    public IReadOnlyList<OpenAiModel> VisibleModels => _models.Where(x => !x.IsHidden).ToList();
    public bool IsLoading { get; private set; }
    public DateTime? LastFetchedAt { get; private set; }

    public event Action? ModelsChanged;

    public OpenAiModelService(IOpenAiAuthService auth)
    {
        _auth = auth as OpenAiAuthService
            ?? throw new InvalidOperationException("OpenAI models require the Codex CLI service.");
        Load();
        EnsureDefaultModel();
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        ModelsChanged?.Invoke();

        try
        {
            var psi = _auth.CreateProcessStartInfo();
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var arg in new[] { "debug", "models" }) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the Codex CLI.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0) return;

            using var document = JsonDocument.Parse(stdout);
            var fetched = EnumerateModelElements(document.RootElement)
                .Select(ParseModel)
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fetched.Count == 0) return;

            var merged = new List<OpenAiModel>
            {
                MergeModel("default", "Codex default"),
            };
            merged.AddRange(fetched
                .Where(x => !string.Equals(x.Id, "default", StringComparison.OrdinalIgnoreCase))
                .Select(x => MergeModel(x.Id, x.Name)));
            _models = merged;
            LastFetchedAt = DateTime.UtcNow;
            Save();
        }
        catch
        {
            // Keep the last known catalog and the always-available CLI default.
        }
        finally
        {
            EnsureDefaultModel();
            IsLoading = false;
            ModelsChanged?.Invoke();
        }
    }

    public void SetDisplayName(string modelId, string name)
    {
        var model = Find(modelId);
        if (model is null) return;
        model.DisplayName = name.Trim();
        model.IsCustomName = true;
        SaveAndNotify();
    }

    public void ResetDisplayName(string modelId)
    {
        var model = Find(modelId);
        if (model is null) return;
        model.DisplayName = InferName(modelId);
        model.IsCustomName = false;
        SaveAndNotify();
    }

    public void ToggleHidden(string modelId)
    {
        var model = Find(modelId);
        if (model is null) return;
        model.IsHidden = !model.IsHidden;
        SaveAndNotify();
    }

    public string GetDisplayName(string modelId) => Find(modelId)?.DisplayName ?? InferName(modelId);

    private OpenAiModel MergeModel(string id, string name)
    {
        var existing = Find(id);
        return new OpenAiModel
        {
            Id = id,
            DisplayName = existing?.IsCustomName == true ? existing.DisplayName : name,
            IsCustomName = existing?.IsCustomName ?? false,
            IsHidden = existing?.IsHidden ?? false,
        };
    }

    private static IEnumerable<JsonElement> EnumerateModelElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (var name in new[] { "models", "data" })
            if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                return array.EnumerateArray().ToArray();
        return [];
    }

    private static (string Id, string Name)? ParseModel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = GetString(element, "slug", "id", "model");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = GetString(element, "display_name", "displayName", "name");
        return (id, string.IsNullOrWhiteSpace(name) ? InferName(id) : name);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string InferName(string id)
    {
        if (string.Equals(id, "default", StringComparison.OrdinalIgnoreCase)) return "Codex default";
        return string.Join(" ", id.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Equals("gpt", StringComparison.OrdinalIgnoreCase)
                ? "GPT" : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private OpenAiModel? Find(string id) =>
        _models.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    private void EnsureDefaultModel()
    {
        if (Find("default") is not null) return;
        _models.Insert(0, new OpenAiModel { Id = "default", DisplayName = "Codex default" });
        Save();
    }

    private void Load()
    {
        try
        {
            _models = JsonSerializer.Deserialize<List<OpenAiModel>>(Preferences.Get(ModelsKey, "[]")) ?? [];
            var fetchedAt = Preferences.Get(FetchedAtKey, "");
            LastFetchedAt = string.IsNullOrWhiteSpace(fetchedAt) ? null : DateTime.Parse(fetchedAt).ToUniversalTime();
        }
        catch { _models = []; }
    }

    private void SaveAndNotify()
    {
        Save();
        ModelsChanged?.Invoke();
    }

    private void Save()
    {
        try
        {
            Preferences.Set(ModelsKey, JsonSerializer.Serialize(_models));
            Preferences.Set(FetchedAtKey, LastFetchedAt?.ToString("O") ?? "");
        }
        catch { }
    }
}
