using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

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
    bool    AiToolsEnabled        { get; set; }  // default: false
    int     AiMaxTurns            { get; set; }  // default: 5 (0 = unlimited)
    string  PreferredExplainModel { get; set; }  // last-used model for Context runs
    string  PreferredReviewModel  { get; set; }  // last-used model for Code Review runs

    // ── MainView layout ────────────────────────────────────────────────────
    bool MainViewSideBySide { get; set; }  // default: false (tabbed)

    // ── Unsupported / experimental features ───────────────────────────────
    bool AnalysisLinksEnabled { get; set; }  // default: false

    // ── Window state ───────────────────────────────────────────────────────
    int WindowX              { get; set; }  // -1 = not yet saved
    int WindowY              { get; set; }
    int WindowWidth          { get; set; }
    int WindowHeight         { get; set; }
    int WindowMonitorLeft    { get; set; }  // WorkArea of the monitor last used
    int WindowMonitorTop     { get; set; }
    int WindowMonitorRight   { get; set; }
    int WindowMonitorBottom  { get; set; }

    event Action? ThemeChanged;
}
