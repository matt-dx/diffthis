using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

public class SettingsService : ISettingsService
{
    private const string ThemeKey          = "theme";
    private const string MaxRecentKey      = "max_recent";
    private const string RecentReposKey    = "recent_repos";
    private const string FontLigaturesKey  = "font_ligatures";
    private const string BranchStatesKey   = "branch_states";
    private const string AiToolsKey           = "ai_tools_enabled";
    private const string AiMaxTurnsKey        = "ai_max_turns";
    private const string MainViewSideBySideKey = "main_view_side_by_side";

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

    public bool MainViewSideBySide
    {
        get => Preferences.Get(MainViewSideBySideKey, false);
        set => Preferences.Set(MainViewSideBySideKey, value);
    }

    public void SaveBranchState(string repoPath, BranchSelectionState state)
    {
        var json = Preferences.Get(BranchStatesKey, "{}");
        var dict = JsonSerializer.Deserialize<Dictionary<string, BranchSelectionState>>(json)
                   ?? new Dictionary<string, BranchSelectionState>();
        dict[repoPath] = state;
        Preferences.Set(BranchStatesKey, JsonSerializer.Serialize(dict));
    }
}
