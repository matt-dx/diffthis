namespace DiffThis.Services;

public enum CopilotAuthState { NotFound, Authenticated }

public interface ICopilotAuthService
{
    CopilotAuthState State     { get; }
    string?          Username  { get; }   // GitHub username, populated asynchronously
    string?          LastError { get; }   // human-readable failure reason, cleared on success

    /// Returns a valid Copilot session token, refreshing if expired or absent.
    Task<string?> GetSessionTokenAsync(CancellationToken ct = default);

    /// Re-check whether gh CLI is available and can obtain a token.
    Task<bool> RefreshAsync(CancellationToken ct = default);
}
