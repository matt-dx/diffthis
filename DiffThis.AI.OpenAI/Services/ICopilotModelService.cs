using DiffThis.Core.Models;
using DiffThis.AI.Shared.Models;

namespace DiffThis.AI.OpenAI.Services;

public interface ICopilotModelService
{
    IReadOnlyList<ClaudeModel> Models        { get; }
    IReadOnlyList<ClaudeModel> VisibleModels { get; }
    bool                       IsLoading     { get; }
    DateTime?                  LastFetchedAt { get; }
    bool                       IsUsingDefaults { get; }  // true when model list came from built-in fallback

    event Action? ModelsChanged;

    Task   RefreshAsync(CancellationToken ct = default);
    void   SetDisplayName(string modelId, string name);
    void   ResetDisplayName(string modelId);
    void   ToggleHidden(string modelId);
    string GetDisplayName(string modelId);
}
