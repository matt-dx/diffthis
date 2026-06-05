using System.Text.Json;
using DiffThis.Core.Models;

namespace DiffThis.Core.Services;

public class SettingsService : ISettingsService
{
    private const string ThemeKey          = "theme";
    private const string MaxRecentKey      = "max_recent";
    private const string RecentReposKey    = "recent_repos";
    private const string FontLigaturesKey  = "font_ligatures";
    private const string BranchStatesKey   = "branch_states";
    private const string AiToolsKey               = "ai_tools_enabled";
    private const string AiMaxTurnsKey            = "ai_max_turns";
    private const string PreferredExplainModelKey = "preferred_explain_model";
    private const string PreferredReviewModelKey  = "preferred_review_model";
    private const string DiffContextLinesKey      = "diff_context_lines";
    private const string MainViewSideBySideKey   = "main_view_side_by_side";
    private const string OllamaEndpointsKey      = "ollama_endpoints";
    private const string AnalysisLinksKey        = "analysis_links_enabled";
    private const string WindowXKey             = "window_x";
    private const string WindowYKey             = "window_y";
    private const string WindowWidthKey         = "window_width";
    private const string WindowHeightKey        = "window_height";
    private const string WindowMonitorLeftKey   = "window_monitor_left";
    private const string WindowMonitorTopKey    = "window_monitor_top";
    private const string WindowMonitorRightKey  = "window_monitor_right";
    private const string WindowMonitorBottomKey = "window_monitor_bottom";

    public event Action? ThemeChanged;

    public AppTheme Theme
    {
        get => (AppTheme)Preferences.Get(ThemeKey, (int)AppTheme.Unspecified);
        set
        {
            Preferences.Set(ThemeKey, (int)value);
            if (Application.Current != null)
                Application.Current.UserAppTheme = value;
            ThemeChanged?.Invoke();
        }
    }

    public int MaxRecentRepositories
    {
        get => Preferences.Get(MaxRecentKey, 10);
        set => Preferences.Set(MaxRecentKey, value);
    }

    public bool FontLigatures
    {
        get => Preferences.Get(FontLigaturesKey, false);
        set => Preferences.Set(FontLigaturesKey, value);
    }

    public List<string> RecentRepositoryPaths
    {
        get
        {
            var json = Preferences.Get(RecentReposKey, "[]");
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        private set => Preferences.Set(RecentReposKey, JsonSerializer.Serialize(value));
    }

    public void AddRecentRepository(string path)
    {
        var repos = RecentRepositoryPaths;
        repos.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        repos.Insert(0, path);
        if (repos.Count > MaxRecentRepositories)
            repos = repos.Take(MaxRecentRepositories).ToList();
        RecentRepositoryPaths = repos;
    }

    public void RemoveRecentRepository(string path)
    {
        var repos = RecentRepositoryPaths;
        repos.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentRepositoryPaths = repos;
    }

    public BranchSelectionState? GetBranchState(string repoPath)
    {
        var json = Preferences.Get(BranchStatesKey, "{}");
        var dict = JsonSerializer.Deserialize<Dictionary<string, BranchSelectionState>>(json);
        return dict is not null && dict.TryGetValue(repoPath, out var state) ? state : null;
    }

    public bool AiToolsEnabled
    {
        get => Preferences.Get(AiToolsKey, false);
        set => Preferences.Set(AiToolsKey, value);
    }

    public int AiMaxTurns
    {
        get => Preferences.Get(AiMaxTurnsKey, 5);
        set => Preferences.Set(AiMaxTurnsKey, value);
    }

    public string PreferredExplainModel
    {
        get => Preferences.Get(PreferredExplainModelKey, "");
        set => Preferences.Set(PreferredExplainModelKey, value);
    }

    public string PreferredReviewModel
    {
        get => Preferences.Get(PreferredReviewModelKey, "");
        set => Preferences.Set(PreferredReviewModelKey, value);
    }

    public int DiffContextLines
    {
        get
        {
            var v = Preferences.Get(DiffContextLinesKey, 3);
            return v is 3 or 10 or 25 or 50 ? v : 3;
        }
        set
        {
            if (value is not (3 or 10 or 25 or 50)) value = 3;
            Preferences.Set(DiffContextLinesKey, value);
        }
    }

    public bool MainViewSideBySide
    {
        get => Preferences.Get(MainViewSideBySideKey, false);
        set => Preferences.Set(MainViewSideBySideKey, value);
    }

    public string OllamaEndpointsJson
    {
        get => Preferences.Get(OllamaEndpointsKey, "[]");
        set => Preferences.Set(OllamaEndpointsKey, value);
    }

    public bool AnalysisLinksEnabled
    {
        get => Preferences.Get(AnalysisLinksKey, false);
        set => Preferences.Set(AnalysisLinksKey, value);
    }

    public int WindowX             { get => Preferences.Get(WindowXKey,             -1);    set => Preferences.Set(WindowXKey,             value); }
    public int WindowY             { get => Preferences.Get(WindowYKey,             -1);    set => Preferences.Set(WindowYKey,             value); }
    public int WindowWidth         { get => Preferences.Get(WindowWidthKey,         1280);  set => Preferences.Set(WindowWidthKey,         value); }
    public int WindowHeight        { get => Preferences.Get(WindowHeightKey,        800);   set => Preferences.Set(WindowHeightKey,        value); }
    public int WindowMonitorLeft   { get => Preferences.Get(WindowMonitorLeftKey,   -1);    set => Preferences.Set(WindowMonitorLeftKey,   value); }
    public int WindowMonitorTop    { get => Preferences.Get(WindowMonitorTopKey,    -1);    set => Preferences.Set(WindowMonitorTopKey,    value); }
    public int WindowMonitorRight  { get => Preferences.Get(WindowMonitorRightKey,  -1);    set => Preferences.Set(WindowMonitorRightKey,  value); }
    public int WindowMonitorBottom { get => Preferences.Get(WindowMonitorBottomKey, -1);    set => Preferences.Set(WindowMonitorBottomKey, value); }

    public void SaveBranchState(string repoPath, BranchSelectionState state)
    {
        var json = Preferences.Get(BranchStatesKey, "{}");
        var dict = JsonSerializer.Deserialize<Dictionary<string, BranchSelectionState>>(json)
                   ?? new Dictionary<string, BranchSelectionState>();
        dict[repoPath] = state;
        Preferences.Set(BranchStatesKey, JsonSerializer.Serialize(dict));
    }

    public void RemoveBranchState(string repoPath)
    {
        var json = Preferences.Get(BranchStatesKey, "{}");
        var dict = JsonSerializer.Deserialize<Dictionary<string, BranchSelectionState>>(json);
        if (dict is null || !dict.ContainsKey(repoPath)) return;
        dict.Remove(repoPath);
        Preferences.Set(BranchStatesKey, JsonSerializer.Serialize(dict));
    }
}
