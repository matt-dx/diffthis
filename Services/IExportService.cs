using DiffThis.Models;

namespace DiffThis.Services;

public interface IExportService
{
    string GenerateMarkdown(DiffResult diff);
    Task ExportMarkdownAsync(DiffResult diff, string filePath);
}
