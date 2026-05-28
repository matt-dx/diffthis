using DiffThis.Models;

namespace DiffThis.Services;

public interface ISettingsService
{
    AppTheme Theme { get; set; }
    int MaxRecentRepositories { get; set; }
    List<string> RecentRepositoryPaths { get; }
    void AddRecentRepository(string path);
    void RemoveRecentRepository(string path);

    event Action? ThemeChanged;
}
