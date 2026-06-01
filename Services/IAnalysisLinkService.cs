using DiffThis.Models;

namespace DiffThis.Services;

public interface IAnalysisLinkService
{
    /// Clear all refs and visibility state (call when diff changes).
    void Reset();

    /// Re-parse all entries for the current diff; replaces any prior refs.
    void Refresh(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> entries);

    /// Query visible refs for a specific diff line (file index + new-file line number).
    IReadOnlyList<AnalysisRef> GetLineRefs(int fileIndex, int newLineNumber);

    /// All visible refs that point to a given file (any line), deduplicated by RunKey+Category.
    IReadOnlyList<AnalysisRef> GetFileRefs(int fileIndex);

    /// True if the ref should be shown (its RunKey is not hidden).
    bool IsVisible(AiRunKey key);

    /// Set visibility of a run key (mirrors the eye-toggle in AnalysisPanel).
    void SetVisible(AiRunKey key, bool visible);

    /// Fired when refs or visibility change — both panels subscribe to trigger re-render.
    event Action? Changed;

    /// Fired when a file/line ref in the analysis is clicked.
    /// Args: fileIndex in DiffResult.Files (–1 if unresolved), lineFrom, lineTo.
    event Action<int, int?, int?>? FocusRequested;

    /// Called by AnalysisPanel when a rendered ref link is clicked.
    void RequestFocus(string rawRef, DiffResult diff);
}
