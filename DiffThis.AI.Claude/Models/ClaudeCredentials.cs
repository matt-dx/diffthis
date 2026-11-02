namespace DiffThis.AI.Claude.Models;

public class ClaudeCredentials
{
    public string AccessToken      { get; set; } = string.Empty;
    public string RefreshToken     { get; set; } = string.Empty;
    public long   ExpiresAt        { get; set; }  // Unix ms
    public string SubscriptionType { get; set; } = string.Empty;

    // Access tokens are short-lived (~8h). ClaudeAuthService refreshes them via the OAuth token endpoint.
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > ExpiresAt;
}
