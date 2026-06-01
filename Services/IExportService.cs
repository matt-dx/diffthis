using DiffThis.Models;

namespace DiffThis.Services;

public interface IExportService
{
    string GenerateMarkdown(DiffResult diff);
    string GenerateMarkdown(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> aiResults);
    Task ExportMarkdownAsync(DiffResult diff, string filePath);
    Task ExportMarkdownAsync(DiffResult diff, IReadOnlyDictionary<AiRunKey, AiCacheEntry> aiResults, string filePath);
}
