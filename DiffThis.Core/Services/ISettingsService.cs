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
    void RemoveBranchState(string repoPath);

    // ── Claude AI defaults ─────────────────────────────────────────────────
    bool    AiToolsEnabled        { get; set; }  // default: false
    int     AiMaxTurns            { get; set; }  // default: 5 (0 = unlimited)
    string  PreferredExplainModel { get; set; }  // last-used model for Context runs
    string  PreferredReviewModel  { get; set; }  // last-used model for Code Review runs

    // ── Diff options ───────────────────────────────────────────────────────
    int DiffContextLines { get; set; }     // lines of context per hunk: 3/10/25/50 (default: 3)

    // ── MainView layout ────────────────────────────────────────────────────
    bool MainViewSideBySide { get; set; }  // default: false (tabbed)

    // ── GitHub Copilot ─────────────────────────────────────────────────────
    int? CopilotTimeoutSeconds { get; set; }  // null = no timeout (default: 300)

    // ── Ollama endpoints ───────────────────────────────────────────────────
    string OllamaEndpointsJson { get; set; }  // JSON-serialised List<PersistedEndpoint>

    // ── Logging ────────────────────────────────────────────────────────────
    bool AiLoggingEnabled { get; set; }  // default: true in Debug, false in Release

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
