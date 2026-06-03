using DiffThis.Core.Models;

namespace DiffThis.AI.Claude.Services;

public interface IClaudeService
{
    Task<string> ReviewDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default);
    Task<string> ExplainDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default);
}
