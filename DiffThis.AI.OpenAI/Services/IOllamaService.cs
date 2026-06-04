using DiffThis.Core.Models;

namespace DiffThis.AI.OpenAI.Services;

public interface IOllamaService
{
    Task<string> ReviewDiffAsync(DiffResult diff, string endpointId, string modelId, CancellationToken ct = default);
    Task<string> ExplainDiffAsync(DiffResult diff, string endpointId, string modelId, CancellationToken ct = default);
}
