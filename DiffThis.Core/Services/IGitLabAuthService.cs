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

    Task<string?> GetTokenAsync();
    Task<bool> RefreshAsync();
    Task<GitLabDeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default);
    void SignOut();
}
