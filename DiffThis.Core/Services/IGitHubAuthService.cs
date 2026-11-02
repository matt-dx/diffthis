namespace DiffThis.Core.Services;

public enum GitHubAuthState { NotFound, PendingDeviceFlow, Authenticated }

/// <summary>
/// Carries the information shown to the user during a GitHub device-code sign-in flow.
/// </summary>
public record GitHubDeviceFlowInfo(
    string UserCode,
    string VerificationUri,
    int    ExpiresIn,
    int    Interval);

public interface IGitHubAuthService
{
    GitHubAuthState State     { get; }
    string?         Username  { get; }   // GitHub username, populated after sign-in
    string?         LastError { get; }   // human-readable failure reason, cleared on success

    /// Fired when <see cref="State"/> or <see cref="Username"/> changes so that
    /// Blazor components can call <c>StateHasChanged</c>.
    event Action? StateChanged;

    /// Completes once startup's stored-credential check has run and <see cref="State"/> reflects
    /// it. A caller that reads <see cref="State"/> right after this service is constructed (e.g.
    /// immediately on page load) should await this first — otherwise it can race the async
    /// SecureStorage read and see a stale <see cref="GitHubAuthState.NotFound"/>.
    Task InitializationTask { get; }

    /// Returns the stored GitHub OAuth token, or null if not authenticated.
    Task<string?> GetTokenAsync();

    /// Re-checks stored credentials without triggering the device flow.
    Task<bool> RefreshAsync();

    /// Begins the standard GitHub OAuth Device Flow using the OAuth App client ID
    /// configured in Settings, and starts background polling. The caller should
    /// display <see cref="GitHubDeviceFlowInfo.UserCode"/> and open
    /// <see cref="GitHubDeviceFlowInfo.VerificationUri"/> in the browser.
    Task<GitHubDeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default);

    /// Clears all stored credentials and returns to <see cref="GitHubAuthState.NotFound"/>.
    void SignOut();
}
