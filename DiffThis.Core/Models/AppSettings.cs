namespace DiffThis.Models;

public class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Unspecified;
    public int MaxRecentRepositories { get; set; } = 10;
}
