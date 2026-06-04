using DiffThis.AI.OpenAI.Models;

namespace DiffThis.AI.OpenAI.Services;

public interface IOllamaEndpointService
{
    IReadOnlyList<OllamaEndpoint> Endpoints { get; }
    bool IsLoading { get; }
    IReadOnlyDictionary<string, string> Errors { get; }

    event Action? Changed;

    void AddEndpoint(string name, string baseUrl, string? apiKey = null);
    void UpdateEndpoint(OllamaEndpoint endpoint);
    void RemoveEndpoint(string endpointId);
    Task RefreshModelsAsync(string endpointId, CancellationToken ct = default);
    Task RefreshAllAsync(CancellationToken ct = default);
    void ToggleModelHidden(string endpointId, string modelId);
    void SetModelDisplayName(string endpointId, string modelId, string displayName);
    void ResetModelDisplayName(string endpointId, string modelId);
    string GetModelDisplayName(string endpointId, string modelId);
    OllamaEndpoint? GetEndpoint(string endpointId);
    IReadOnlyList<OllamaModel> GetVisibleModels(string endpointId);
    IReadOnlyList<OllamaModel> GetAllModels(string endpointId);

    Task StartPullAsync(string endpointId, string modelId);
    void CancelPull(string endpointId, string modelId);
    void ClearModelError(string endpointId, string modelId);
    bool IsModelPulling(string endpointId, string modelId);
    string? GetPullStatus(string endpointId, string modelId);
    string? GetPullError(string endpointId, string modelId);
}
