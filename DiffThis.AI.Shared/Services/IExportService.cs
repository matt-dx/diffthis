using DiffThis.AI.Shared.Models;
using DiffThis.Core.Models;

namespace DiffThis.AI.Shared.Services;

public interface IExportService
{
    string GenerateMarkdown(DiffResult diff);
    string GenerateMarkdown(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> aiResults);
    Task ExportMarkdownAsync(DiffResult diff, string filePath);
    Task ExportMarkdownAsync(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> aiResults, string filePath);
}
