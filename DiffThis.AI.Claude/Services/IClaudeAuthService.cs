namespace DiffThis.AI.Claude.Services;

public enum ClaudeAuthState { NotFound, Authenticated }

public interface IClaudeAuthService
{
    ClaudeAuthState State            { get; }
    string?         AccessToken      { get; }
    string?         SubscriptionType { get; }
    string?         Email            { get; }
    bool            IsTokenExpired   { get; }

    /// Re-reads the credentials file. Call after `claude auth login`.
    void Reload();

    /// Refreshes the OAuth access token via the Anthropic token endpoint, then reloads from disk.
    Task<bool> RefreshAsync();

    /// Launches `claude auth login` in a visible console window, waits for it to complete, then
    /// reloads credentials from disk. Returns whether authentication succeeded.
    Task<bool> TryInteractiveLoginAsync(CancellationToken ct = default);
}
