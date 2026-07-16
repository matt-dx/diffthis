using DiffThis.Core.Models;

namespace DiffThis.AI.OpenAI.Services;

public interface IOpenAiService
{
    Task<string> ReviewDiffAsync(DiffResult diff, string model, CancellationToken ct = default);
    Task<string> ExplainDiffAsync(DiffResult diff, string model, CancellationToken ct = default);
}
