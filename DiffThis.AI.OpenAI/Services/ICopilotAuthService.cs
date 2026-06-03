namespace DiffThis.AI.OpenAI.Services;

public enum CopilotAuthState { NotFound, PendingDeviceFlow, Authenticated }

/// <summary>
/// Carries the information shown to the user during a device-code sign-in flow.
/// </summary>
public record DeviceFlowInfo(
    string UserCode,
    string VerificationUri,
    int    ExpiresIn,
    int    Interval);

public interface ICopilotAuthService
{
    CopilotAuthState State     { get; }
    string?          Username  { get; }   // GitHub username, populated after sign-in
    string?          LastError { get; }   // human-readable failure reason, cleared on success

    /// Fired when <see cref="State"/> or <see cref="Username"/> changes so that
    /// Blazor components can call <c>StateHasChanged</c>.
    event Action? StateChanged;

    /// Returns a valid Copilot session token, exchanging the stored OAuth token
    /// if the session token is missing or expired.  Returns <c>null</c> if not
    /// authenticated.
    Task<string?> GetSessionTokenAsync(CancellationToken ct = default);

    /// Re-checks stored credentials without triggering the device flow.
    Task<bool> RefreshAsync(CancellationToken ct = default);

    /// Begins the GitHub device-code OAuth flow using the Copilot OAuth app and
    /// starts background polling.  The caller should display
    /// <see cref="DeviceFlowInfo.UserCode"/> and open
    /// <see cref="DeviceFlowInfo.VerificationUri"/> in the browser.
    Task<DeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default);

    /// Clears all stored credentials and returns to <see cref="CopilotAuthState.NotFound"/>.
    void SignOut();
}
