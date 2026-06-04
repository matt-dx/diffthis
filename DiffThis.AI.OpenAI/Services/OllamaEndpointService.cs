using System.Text;
using System.Text.Json;
using DiffThis.AI.OpenAI.Models;
using DiffThis.Core.Services;
using Microsoft.Extensions.Logging;

namespace DiffThis.AI.OpenAI.Services;

public class OllamaEndpointService : IOllamaEndpointService
{
    private readonly ISettingsService               _settings;
    private readonly ILogger<OllamaEndpointService> _log;
    private readonly HttpClient                     _http;

    private List<PersistedEndpoint>    _store  = [];
    private int                        _loadingCount;
    private Dictionary<string, string> _errors = [];

    // endpointId -> modelId -> (status text, cancellation)
    private readonly Dictionary<string, Dictionary<string, (string Status, CancellationTokenSource Cts)>> _pulls = new();

    // Separate HttpClient with no global timeout for long-running pull streams
    private readonly HttpClient _pullHttp = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout                    = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion      = System.Net.HttpVersion.Version11,
        DefaultVersionPolicy       = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
    };

    public IReadOnlyList<OllamaEndpoint> Endpoints =>
        _store.Select(s => new OllamaEndpoint(s.Id, s.Name, s.BaseUrl, s.ApiKey, s.IconId, s.BadgeColor)).ToList();

    public bool IsLoading => _loadingCount > 0;

    // Last error per endpoint ID, cleared on successful refresh
    public IReadOnlyDictionary<string, string> Errors => _errors;

    public event Action? Changed;

    public OllamaEndpointService(ISettingsService settings, ILogger<OllamaEndpointService> log)
    {
        _settings = settings;
        _log      = log;

        // Force HTTP/1.1 — avoids version-negotiation hangs on local endpoints,
        // and avoids Windows AppContainer IPv6-vs-IPv4 ambiguity on "localhost".
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion      = System.Net.HttpVersion.Version11,
            DefaultVersionPolicy       = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
        };

        Load();
        if (_store.Count > 0)
            _ = RecoverAndRefreshAsync();
    }

    // ── CRUD ──────────────────────────────────────────────────────────────

    public void AddEndpoint(string name, string baseUrl, string? apiKey = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _store.Add(new PersistedEndpoint { Id = id, Name = name, BaseUrl = baseUrl, ApiKey = apiKey });
        Save();
        Changed?.Invoke();
        _ = RefreshModelsAsync(id);
    }

    public void UpdateEndpoint(OllamaEndpoint endpoint)
    {
        var s = _store.FirstOrDefault(x => x.Id == endpoint.Id);
        if (s is null) return;
        s.Name       = endpoint.Name;
        s.BaseUrl    = endpoint.BaseUrl;
        s.ApiKey     = endpoint.ApiKey;
        s.IconId     = endpoint.IconId;
        s.BadgeColor = endpoint.BadgeColor;
        Save();
        Changed?.Invoke();
    }

    public void RemoveEndpoint(string endpointId)
    {
        if (_pulls.TryGetValue(endpointId, out var ep))
        {
            foreach (var (_, (_, cts)) in ep) cts.Cancel();
            _pulls.Remove(endpointId);
        }
        _store.RemoveAll(s => s.Id == endpointId);
        _errors.Remove(endpointId);
        Save();
        Changed?.Invoke();
    }

    // ── Model refresh ─────────────────────────────────────────────────────

    public async Task RefreshModelsAsync(string endpointId, CancellationToken ct = default)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return;

        Interlocked.Increment(ref _loadingCount);
        Changed?.Invoke();

        try
        {
            var baseUrl = stored.BaseUrl.TrimEnd('/');
            List<(string id, string display)> fetched;

            try
            {
                fetched = await FetchAllModelsAsync(baseUrl, stored.ApiKey, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsTimeout(ex)
                && baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                // localhost timed out — retry with 127.0.0.1 to avoid IPv6/IPv4 ambiguity
                var altUrl = baseUrl.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
                _log.LogDebug("Ollama: localhost timed out, retrying with {AltUrl}", altUrl);
                fetched = await FetchAllModelsAsync(altUrl, stored.ApiKey, ct);
                // Persist the working URL so future fetches succeed immediately
                stored.BaseUrl = altUrl;
                _log.LogInformation("Ollama: updated endpoint {Id} URL to {Url}", endpointId, altUrl);
            }

            var merged = new List<PersistedModel>();
            foreach (var (id, display) in fetched)
            {
                var existing = stored.Models.FirstOrDefault(m => m.Id == id);
                merged.Add(new PersistedModel
                {
                    Id          = id,
                    DisplayName = display,
                    IsHidden    = existing?.IsHidden ?? false,
                });
            }
            // Preserve pull-error and in-progress entries not yet returned by the server
            foreach (var m in stored.Models.Where(m => m.PullError is not null || m.IsPulling))
            {
                if (!merged.Any(x => x.Id == m.Id))
                    merged.Add(m);
            }
            stored.Models = merged;
            _errors.Remove(endpointId);
            Save();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ollama: failed to refresh models for endpoint {Id} ({Type}: {Msg})",
                endpointId, ex.GetType().Name, ex.Message);
            _errors[endpointId] = BuildErrorHint(stored.BaseUrl, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _loadingCount);
            Changed?.Invoke();
        }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var ep in _store.ToList())
        {
            if (ct.IsCancellationRequested) break;
            await RefreshModelsAsync(ep.Id, ct);
        }
    }

    private async Task RecoverAndRefreshAsync()
    {
        // Collect models that were mid-pull when the app last closed
        var stale = _store
            .SelectMany(ep => ep.Models
                .Where(m => m.IsPulling)
                .Select(m => (EndpointId: ep.Id, ModelId: m.Id)))
            .ToList();

        await RefreshAllAsync();

        // For each stale pull, check if the model is now available (pull completed while
        // the app was closed) or still absent (restart the pull to get live progress/errors).
        foreach (var (endpointId, modelId) in stale)
        {
            var stored = _store.FirstOrDefault(x => x.Id == endpointId);
            if (stored is null) continue;

            var isNowReal = stored.Models.Any(m => m.Id == modelId && !m.IsPulling && m.PullError is null);
            if (!isNowReal)
                await StartPullAsync(endpointId, modelId);
        }
    }

    // ── Model management ──────────────────────────────────────────────────

    public void ToggleModelHidden(string endpointId, string modelId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return;
        var m = stored.Models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.IsHidden = !m.IsHidden;
        Save();
        Changed?.Invoke();
    }

    public void SetModelDisplayName(string endpointId, string modelId, string displayName)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return;
        var m = stored.Models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.CustomName = displayName;
        Save();
        Changed?.Invoke();
    }

    public void ResetModelDisplayName(string endpointId, string modelId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return;
        var m = stored.Models.FirstOrDefault(x => x.Id == modelId);
        if (m is null) return;
        m.CustomName = null;
        Save();
        Changed?.Invoke();
    }

    public string GetModelDisplayName(string endpointId, string modelId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return modelId;
        return stored.Models.FirstOrDefault(x => x.Id == modelId)?.DisplayName ?? modelId;
    }

    public OllamaEndpoint? GetEndpoint(string endpointId)
    {
        var s = _store.FirstOrDefault(x => x.Id == endpointId);
        return s is null ? null : new OllamaEndpoint(s.Id, s.Name, s.BaseUrl, s.ApiKey, s.IconId, s.BadgeColor);
    }

    public IReadOnlyList<OllamaModel> GetVisibleModels(string endpointId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return [];
        return stored.Models
            .Where(m => !m.IsHidden && !m.IsPulling && m.PullError is null)
            .Select(m => new OllamaModel { Id = m.Id, DisplayName = m.DisplayName })
            .ToList();
    }

    public IReadOnlyList<OllamaModel> GetAllModels(string endpointId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return [];
        return stored.Models
            .Select(m => new OllamaModel
            {
                Id           = m.Id,
                DisplayName  = m.CustomName ?? m.DisplayName,
                IsCustomName = m.CustomName is not null,
                IsHidden     = m.IsHidden,
                IsPulling    = m.IsPulling,
                PullError    = m.PullError,
            })
            .ToList();
    }

    public void ClearModelError(string endpointId, string modelId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return;
        stored.Models.RemoveAll(m => m.Id == modelId && m.PullError is not null);
        Save();
        Changed?.Invoke();
    }

    // ── Pull model ────────────────────────────────────────────────────────

    public bool   IsModelPulling(string endpointId, string modelId) =>
        _pulls.TryGetValue(endpointId, out var ep) && ep.ContainsKey(modelId);

    public string? GetPullStatus(string endpointId, string modelId) =>
        _pulls.TryGetValue(endpointId, out var ep) && ep.TryGetValue(modelId, out var s) ? s.Status : null;

    public string? GetPullError(string endpointId, string modelId)
    {
        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        return stored?.Models.FirstOrDefault(m => m.Id == modelId)?.PullError;
    }

    public Task StartPullAsync(string endpointId, string modelId)
    {
        modelId = modelId.Trim();
        if (string.IsNullOrEmpty(modelId)) return Task.CompletedTask;

        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is null) return Task.CompletedTask;

        if (modelId.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase))
        {
            // Cloud-routed models proxy to an external API and need provider keys in the container.
            stored.Models.RemoveAll(m => m.Id == modelId && (m.IsPulling || m.PullError is not null));
            stored.Models.Add(new PersistedModel
            {
                Id          = modelId,
                DisplayName = InferDisplayName(modelId),
                IsHidden    = true,
                PullError   = $"\"{modelId}\" is a cloud-routed model. Pulling cloud models via DiffThis " +
                              "is not supported — use the Ollama CLI directly: ollama pull {modelId}. " +
                              "Once available, the model will appear here after a Refresh and can be used normally, " +
                              "provided the required provider API key is set in your Ollama container " +
                              "(e.g. MINIMAX_API_KEY for MiniMax models).",
            });
            Save();
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        if (IsModelPulling(endpointId, modelId)) return Task.CompletedTask;

        // Remove any prior error/pulling placeholder for this model so we start fresh
        stored.Models.RemoveAll(m => m.Id == modelId && (m.IsPulling || m.PullError is not null));

        // If the model already exists as a real (non-error) entry, nothing to do
        if (stored.Models.Any(m => m.Id == modelId))
            return Task.CompletedTask;

        // Add a locked placeholder so it appears immediately in the list
        stored.Models.Add(new PersistedModel
        {
            Id = modelId, DisplayName = InferDisplayName(modelId), IsHidden = true, IsPulling = true
        });
        Save();

        var cts = new CancellationTokenSource();
        if (!_pulls.ContainsKey(endpointId)) _pulls[endpointId] = new();
        _pulls[endpointId][modelId] = ("Starting…", cts);
        Changed?.Invoke();

        _ = Task.Run(() => DoPullAsync(endpointId, modelId, stored.BaseUrl, stored.ApiKey, cts.Token));
        return Task.CompletedTask;
    }

    public void CancelPull(string endpointId, string modelId)
    {
        if (_pulls.TryGetValue(endpointId, out var ep) && ep.TryGetValue(modelId, out var state))
            state.Cts.Cancel();
    }

    private void SetPullStatus(string endpointId, string modelId, string status)
    {
        if (_pulls.TryGetValue(endpointId, out var ep) && ep.ContainsKey(modelId))
        {
            ep[modelId] = (status, ep[modelId].Cts);
            Changed?.Invoke();
        }
    }

    private async Task SetPullSucceeded(string endpointId, string modelId)
    {
        if (_pulls.TryGetValue(endpointId, out var ep)) ep.Remove(modelId);

        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is not null)
        {
            stored.Models.RemoveAll(m => m.Id == modelId && m.IsPulling);
            Save();
        }

        // Refresh so the real entry (from Ollama) replaces the placeholder
        await RefreshModelsAsync(endpointId);
    }

    private void SetPullFailed(string endpointId, string modelId, string reason)
    {
        if (_pulls.TryGetValue(endpointId, out var ep)) ep.Remove(modelId);

        var stored = _store.FirstOrDefault(x => x.Id == endpointId);
        if (stored is not null)
        {
            // Transition placeholder from IsPulling → PullError (keep it in the list)
            var placeholder = stored.Models.FirstOrDefault(m => m.Id == modelId && m.IsPulling);
            if (placeholder is not null)
            {
                placeholder.IsPulling = false;
                placeholder.PullError = reason;
            }
            Save();
        }

        Changed?.Invoke();
    }

    private async Task DoPullAsync(string endpointId, string modelId,
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        try
        {
            var url  = $"{baseUrl.TrimEnd('/')}/api/pull";
            var body = JsonSerializer.Serialize(new { model = modelId, stream = true });

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Headers.Add("User-Agent", "DiffThis/1.0");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _pullHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                // Try to extract error from JSON
                try
                {
                    using var d = JsonDocument.Parse(errBody);
                    if (d.RootElement.TryGetProperty("error", out var ep2))
                        errBody = ep2.GetString() ?? errBody;
                }
                catch { /* use raw body */ }
                SetPullFailed(endpointId, modelId, $"Server error {(int)resp.StatusCode}: {errBody}");
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errProp))
                    {
                        SetPullFailed(endpointId, modelId, errProp.GetString() ?? "Unknown error");
                        return;
                    }

                    if (root.TryGetProperty("status", out var statusProp))
                    {
                        var status = statusProp.GetString() ?? "";

                        if (status == "success")
                        {
                            await SetPullSucceeded(endpointId, modelId);
                            return;
                        }

                        // Build human-readable progress string
                        string display;
                        if (root.TryGetProperty("total", out var totalProp) && totalProp.GetInt64() > 0
                         && root.TryGetProperty("completed", out var completedProp))
                        {
                            var pct = (int)(100.0 * completedProp.GetInt64() / totalProp.GetInt64());
                            display = $"{status} ({pct}%)";
                        }
                        else
                        {
                            display = status;
                        }

                        SetPullStatus(endpointId, modelId, display);
                    }
                }
                catch (JsonException) { /* skip malformed lines */ }
            }

            if (ct.IsCancellationRequested)
                SetPullFailed(endpointId, modelId, "Pull was cancelled.");
            else
                SetPullFailed(endpointId, modelId, "Pull ended without confirmation from server.");
        }
        catch (OperationCanceledException)
        {
            SetPullFailed(endpointId, modelId, "Pull was cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ollama pull failed for {Model} on endpoint {Id}", modelId, endpointId);
            SetPullFailed(endpointId, modelId, ex.Message);
        }
    }

    // ── Fetch helpers ─────────────────────────────────────────────────────

    private async Task<List<(string id, string display)>> FetchAllModelsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        // Prefer native /api/tags; fall back to OpenAI-compat /v1/models
        try
        {
            _log.LogDebug("Ollama: GET {Url}/api/tags", baseUrl);
            var result = await FetchFromApiTagsAsync(baseUrl, apiKey, ct);
            _log.LogDebug("Ollama: {Count} models via /api/tags", result.Count);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!IsTimeout(ex))
        {
            _log.LogDebug(ex, "Ollama: /api/tags failed ({Msg}), trying /v1/models", ex.Message);
            var result = await FetchFromV1ModelsAsync(baseUrl, apiKey, ct);
            _log.LogDebug("Ollama: {Count} models via /v1/models", result.Count);
            return result;
        }
    }

    private static bool IsTimeout(Exception ex) =>
        ex is TaskCanceledException or TimeoutException
        || ex.Message.Contains("Timeout",    StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("timed out",  StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase);

    private async Task<List<(string id, string display)>> FetchFromApiTagsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/tags");
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
        req.Headers.Add("User-Agent", "DiffThis/1.0");

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        return doc.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(e =>
            {
                var name = e.GetProperty("name").GetString() ?? "";
                return (id: name, display: InferDisplayName(name));
            })
            .Where(x => !string.IsNullOrEmpty(x.id))
            .ToList();
    }

    private async Task<List<(string id, string display)>> FetchFromV1ModelsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
        req.Headers.Add("User-Agent", "DiffThis/1.0");

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root  = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("data");

        return array.EnumerateArray()
            .Select(e =>
            {
                var id = e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                return (id, display: InferDisplayName(id));
            })
            .Where(x => !string.IsNullOrEmpty(x.id))
            .ToList();
    }

    private static string BuildErrorHint(string baseUrl, Exception ex)
    {
        var msg = ex.Message;
        if (ex is TaskCanceledException || msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                                        || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connection timed out reaching {baseUrl}. " +
                   "If using localhost, try http://127.0.0.1:11434 (avoids IPv6 ambiguity). " +
                   "If the app is installed/packaged, run: " +
                   "CheckNetIsolation.exe LoopbackExempt -a -n=<PackageFamilyName>";
        }
        if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return $"Connection refused at {baseUrl}. Check that Ollama is running and the port is correct.";
        return msg;
    }

    private static string InferDisplayName(string modelId)
    {
        // "llama3.2:latest" → "Llama3.2", "mistral:7b" → "Mistral"
        var colon = modelId.IndexOf(':');
        var name  = colon >= 0 ? modelId[..colon] : modelId;
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name;
    }

    // ── Persistence ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            var json = _settings.OllamaEndpointsJson;
            _store = JsonSerializer.Deserialize<List<PersistedEndpoint>>(json) ?? [];
            // IsPulling entries are intentionally kept so RecoverAndRefreshAsync can check them
        }
        catch { _store = []; }
    }

    private void Save()
    {
        try { _settings.OllamaEndpointsJson = JsonSerializer.Serialize(_store); }
        catch { /* best effort */ }
    }

    // ── Internal persisted types ──────────────────────────────────────────

    private class PersistedEndpoint
    {
        public string  Id         { get; set; } = "";
        public string  Name       { get; set; } = "";
        public string  BaseUrl    { get; set; } = "http://localhost:11434";
        public string? ApiKey     { get; set; }
        public string  IconId     { get; set; } = "ollama";
        public string  BadgeColor { get; set; } = "#D68E42";
        public List<PersistedModel> Models { get; set; } = [];
    }

    private class PersistedModel
    {
        public string  Id          { get; set; } = "";
        public string  DisplayName { get; set; } = "";
        public string? CustomName  { get; set; }
        public bool    IsHidden    { get; set; }
        public bool    IsPulling   { get; set; }
        public string? PullError   { get; set; }
    }
}
