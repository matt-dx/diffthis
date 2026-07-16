using DiffThis.AI.OpenAI.Models;

namespace DiffThis.AI.OpenAI.Services;

public interface IOpenAiModelService
{
    IReadOnlyList<OpenAiModel> Models        { get; }
    IReadOnlyList<OpenAiModel> VisibleModels { get; }
    bool                       IsLoading     { get; }
    DateTime?                  LastFetchedAt { get; }

    event Action? ModelsChanged;

    Task RefreshAsync(CancellationToken ct = default);
    void SetDisplayName(string modelId, string name);
    void ResetDisplayName(string modelId);
    void ToggleHidden(string modelId);
    string GetDisplayName(string modelId);
}
