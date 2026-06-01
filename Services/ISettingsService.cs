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

    // ── Claude AI defaults ─────────────────────────────────────────────────
    bool AiToolsEnabled { get; set; }  // default: false
    int  AiMaxTurns     { get; set; }  // default: 5 (0 = unlimited)

    // ── MainView layout ────────────────────────────────────────────────────
    bool MainViewSideBySide { get; set; }  // default: false (tabbed)

    event Action? ThemeChanged;
}
