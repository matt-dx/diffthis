using DiffThis.Models;

namespace DiffThis.Services;

// Shared singleton for passing DiffResult between pages without serializing to query params.
public class DiffSessionService
{
    public DiffResult? CurrentDiff { get; set; }
    public HashSet<int> HiddenFiles { get; } = [];

    public void ResetDiffState()
    {
        HiddenFiles.Clear();
    }
}
