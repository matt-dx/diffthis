namespace DiffThis.Core.Services;

public enum GitLabAuthState { NotFound, PendingDeviceFlow, Authenticated }

public record GitLabDeviceFlowInfo(
    string UserCode,
    string VerificationUri,
    int    ExpiresIn,
    int    Interval);

public interface IGitLabAuthService
{
    GitLabAuthState State     { get; }
    string?         Username  { get; }
    string?         LastError { get; }

    event Action? StateChanged;

    /// Completes once startup's stored-credential check has run and <see cref="State"/> reflects
    /// it — await before reading <see cref="State"/> right after construction (e.g. on page load)
    /// to avoid racing the async SecureStorage read.
    Task InitializationTask { get; }

    Task<string?> GetTokenAsync();
    Task<bool> RefreshAsync();
    Task<GitLabDeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default);
    void SignOut();
}
