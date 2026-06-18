using DiffThis.AI.OpenAI.Models;

namespace DiffThis.AI.OpenAI.Services;

public interface ICopilotModelService
{
    IReadOnlyList<CopilotModel> Models        { get; }
    IReadOnlyList<CopilotModel> VisibleModels { get; }
    bool                       IsLoading     { get; }
    DateTime?                  LastFetchedAt { get; }
    bool                       IsUsingDefaults { get; }  // true when model list came from built-in fallback

    event Action? ModelsChanged;

    Task   RefreshAsync(CancellationToken ct = default);
    void   SetDisplayName(string modelId, string name);
    void   ResetDisplayName(string modelId);
    void   ToggleHidden(string modelId);
    string GetDisplayName(string modelId);

    /// Returns the reasoning_effort value to send in a request (null = omit the parameter).
    /// Falls back to "low" for known thinking models when no user preference is set.
    string? GetReasoningEffort(string modelId);
    void    SetReasoningEffort(string modelId, string? effort);
}
