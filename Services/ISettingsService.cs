using DiffThis.Models;

namespace DiffThis.Services;

public interface ISettingsService
{
    AppTheme Theme { get; set; }
    int MaxRecentRepositories { get; set; }
    bool FontLigatures { get; set; }
    List<string> RecentRepositoryPaths { get; }
    void AddRecentRepository(string path);
    void RemoveRecentRepository(string path);
    BranchSelectionState? GetBranchState(string repoPath);
    void SaveBranchState(string repoPath, BranchSelectionState state);

    event Action? ThemeChanged;
}
