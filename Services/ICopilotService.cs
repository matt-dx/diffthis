using DiffThis.Models;

namespace DiffThis.Services;

public interface ICopilotService
{
    Task<string> ReviewDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default);
    Task<string> ExplainDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default);
}
