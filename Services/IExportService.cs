using DiffThis.Models;

namespace DiffThis.Services;

public interface IExportService
{
    string GenerateMarkdown(DiffResult diff);
    string GenerateHtml(DiffResult diff, bool darkMode);
    Task ExportMarkdownAsync(DiffResult diff, string filePath);
    Task ExportHtmlAsync(DiffResult diff, string filePath, bool darkMode);
}
