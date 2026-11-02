namespace DiffThis.Core.Services;

/// Shared staleness check used by the AI model-catalog services (Claude, Copilot,
/// OpenAI) to decide whether to fetch an updated model list in the background.
public static class ModelAutoRefresh
{
    public static bool ShouldRefresh(int modelCount, DateTime? lastFetchedAt, ISettingsService settings)
    {
        // Nothing cached yet — always fetch regardless of the auto-refresh setting.
        if (modelCount == 0 || lastFetchedAt is null) return true;

        if (!settings.AutoRefreshModelsEnabled) return false;

        var age = DateTime.UtcNow - lastFetchedAt.Value;
        return age > TimeSpan.FromHours(settings.AutoRefreshModelsIntervalHours);
    }
}
