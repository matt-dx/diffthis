using DiffThis.Models;

namespace DiffThis.Services;

public interface IClaudeService
{
    string[] AvailableModels { get; }
    Task<string> ReviewDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default);
    Task<string> ExplainDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default);
}
