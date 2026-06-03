using DiffThis.Core.Models;
using DiffThis.AI.Shared.Models;

namespace DiffThis.AI.Claude.Services;

public interface IClaudeModelService
{
    IReadOnlyList<ClaudeModel> Models        { get; }
    IReadOnlyList<ClaudeModel> VisibleModels { get; }
    DateTime?                  LastFetchedAt { get; }
    bool                       IsLoading    { get; }

    /// Fired whenever the model list or any display name changes.
    event Action? ModelsChanged;

    /// Fetch the latest model list from the Anthropic API. No-op if already loading.
    Task RefreshAsync(CancellationToken ct = default);

    /// Override the display name shown for a model.
    void SetDisplayName(string modelId, string name);

    /// Revert to the algorithmically inferred display name.
    void ResetDisplayName(string modelId);

    /// Toggle whether a model appears in the model-selection dropdowns.
    void ToggleHidden(string modelId);

    /// Returns the display name for a model ID (custom or inferred).
    string GetDisplayName(string modelId);
}
