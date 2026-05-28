using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

public class SettingsService : ISettingsService
{
    private const string ThemeKey = "theme";
    private const string MaxRecentKey = "max_recent";
    private const string RecentReposKey = "recent_repos";

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
}
