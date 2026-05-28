using DiffThis.Models;

namespace DiffThis.Services;

// Shared singleton for passing DiffResult between pages without serializing to query params.
public class DiffSessionService
{
    public DiffResult? CurrentDiff { get; set; }
}
