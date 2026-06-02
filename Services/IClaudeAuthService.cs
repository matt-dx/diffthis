namespace DiffThis.Services;

public enum ClaudeAuthState { NotFound, Authenticated }

public interface IClaudeAuthService
{
    ClaudeAuthState State            { get; }
    string?         AccessToken      { get; }
    string?         SubscriptionType { get; }
    string?         Email            { get; }

    /// Re-reads the credentials file. Call after `claude auth login` or after a token refresh.
    void Reload();

    /// Reloads the credentials file from disk and returns true if the user is authenticated.
    Task<bool> RefreshAsync();
}
