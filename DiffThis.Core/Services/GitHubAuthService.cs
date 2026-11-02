using System.Text.Json;

namespace DiffThis.Core.Services;

/// <summary>
/// Authenticates with GitHub via the standard OAuth Device Flow, so pull requests can be
/// browsed without ever requiring the user to open a terminal.
///
/// Device Flow needs no client secret, but it does need a <c>client_id</c>. DiffThis ships
/// with its own registered OAuth App's Client ID (public by design — the same way the `gh`
/// CLI's or VS Code's client IDs are public) so sign-in works out of the box; Settings still
/// lets a user override it with their own App's Client ID if they'd rather DiffThis show up
/// under a different name on their GitHub authorized-apps page.
/// </summary>
public class GitHubAuthService : IGitHubAuthService
{
    private const string DefaultClientId = "Ov23liEw5usMhTBqUdqX";

    private const string OAuthTokenKey = "github_oauth_token";
    private const string UsernameKey   = "github_username";

    private const string DeviceCodeUrl  = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    private readonly ISettingsService _settings;
    private readonly HttpClient       _http = new();

    private CancellationTokenSource? _pollCts;

    public GitHubAuthState State     { get; private set; } = GitHubAuthState.NotFound;
    public Task InitializationTask { get; }
    public string?         Username  { get; private set; }
    public string?         LastError { get; private set; }

    public event Action? StateChanged;

    public GitHubAuthService(ISettingsService settings)
    {
        _settings = settings;
        Username  = Preferences.Get(UsernameKey, (string?)null);
        InitializationTask = InitializeStateAsync();
    }

    public Task<string?> GetTokenAsync() => GetStoredTokenAsync();

    public async Task<bool> RefreshAsync()
    {
        var stored = await GetStoredTokenAsync();
        State = string.IsNullOrEmpty(stored) ? GitHubAuthState.NotFound : GitHubAuthState.Authenticated;
        StateChanged?.Invoke();
        return State == GitHubAuthState.Authenticated;
    }

    // ── Device-code flow ──────────────────────────────────────────────────

    public async Task<GitHubDeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        var clientId = _settings.GitHubOAuthClientId.Trim();
        if (string.IsNullOrEmpty(clientId)) clientId = DefaultClientId;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"]     = "repo",
            });
            req.Headers.Add("Accept",     "application/json");
            req.Headers.Add("User-Agent", "DiffThis/1.0");

            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errProp))
            {
                LastError = root.TryGetProperty("error_description", out var desc)
                    ? desc.GetString() : errProp.GetString();
                State = GitHubAuthState.NotFound;
                StateChanged?.Invoke();
                return null;
            }

            var deviceCode = root.GetProperty("device_code").GetString()!;
            var userCode   = root.GetProperty("user_code").GetString()!;
            var verifyUri  = root.GetProperty("verification_uri").GetString()!;
            var expiresIn  = root.GetProperty("expires_in").GetInt32();
            var interval   = root.GetProperty("interval").GetInt32();

            State     = GitHubAuthState.PendingDeviceFlow;
            LastError = null;
            StateChanged?.Invoke();

            // Kick off background polling — fires StateChanged when done
            _pollCts = new CancellationTokenSource();
            _ = PollForTokenAsync(clientId, deviceCode, interval, expiresIn, _pollCts.Token);

            return new GitHubDeviceFlowInfo(userCode, verifyUri, expiresIn, interval);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            State     = GitHubAuthState.NotFound;
            LastError = $"Failed to start sign-in: {ex.Message}";
            StateChanged?.Invoke();
            return null;
        }
    }

    private async Task PollForTokenAsync(
        string clientId, string deviceCode, int interval, int expiresIn, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(interval, 5)), ct);
            }
            catch (OperationCanceledException) { break; }

            if (ct.IsCancellationRequested) break;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl);
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"]   = clientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
                });
                req.Headers.Add("Accept",     "application/json");
                req.Headers.Add("User-Agent", "DiffThis/1.0");

                var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errProp))
                {
                    var error = errProp.GetString();
                    if (error == "slow_down" && root.TryGetProperty("interval", out var newInt))
                        interval = newInt.GetInt32();
                    else if (error is not ("authorization_pending" or "slow_down"))
                    {
                        LastError = root.TryGetProperty("error_description", out var desc)
                            ? desc.GetString() : error;
                        State = GitHubAuthState.NotFound;
                        StateChanged?.Invoke();
                        return;
                    }
                    continue;
                }

                if (!root.TryGetProperty("access_token", out var tokenProp)) continue;
                var token = tokenProp.GetString();
                if (string.IsNullOrEmpty(token)) continue;

                // Success — store token and update state
                if (!await TryStoreTokenAsync(token))
                {
                    State     = GitHubAuthState.NotFound;
                    LastError = "Unable to securely store GitHub credentials on this device.";
                    StateChanged?.Invoke();
                    return;
                }

                State     = GitHubAuthState.Authenticated;
                LastError = null;
                StateChanged?.Invoke();

                // Fetch username asynchronously
                _ = TryReadUsernameAsync(token);
                return;
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep polling on transient errors */ }
        }

        // Flow expired or was cancelled — only reset if we're still pending
        if (State == GitHubAuthState.PendingDeviceFlow)
        {
            State     = GitHubAuthState.NotFound;
            LastError = ct.IsCancellationRequested
                ? "Sign-in was cancelled."
                : "Sign-in timed out. Please try again.";
            StateChanged?.Invoke();
        }
    }

    // ── SignOut ───────────────────────────────────────────────────────────

    public void SignOut()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        RemoveStoredToken();
        Preferences.Remove(UsernameKey);

        Username  = null;
        State     = GitHubAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    // ── Username fetch ────────────────────────────────────────────────────

    private async Task TryReadUsernameAsync(string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Add("Authorization", $"token {token}");
            req.Headers.Add("User-Agent",    "DiffThis/1.0");
            req.Headers.Add("Accept",        "application/vnd.github+json");

            var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("login", out var login)) return;

            Username = login.GetString();
            Preferences.Set(UsernameKey, Username ?? "");
            StateChanged?.Invoke();
        }
        catch { /* best effort */ }
    }

    private async Task InitializeStateAsync()
    {
        try
        {
            var stored = await GetStoredTokenAsync();
            if (!string.IsNullOrEmpty(stored))
            {
                State = GitHubAuthState.Authenticated;
                StateChanged?.Invoke();
            }
        }
        catch
        {
            // best effort
        }
    }

    private static async Task<bool> TryStoreTokenAsync(string token)
    {
        try
        {
            await SecureStorage.Default.SetAsync(OAuthTokenKey, token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> GetStoredTokenAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(OAuthTokenKey);
            if (!string.IsNullOrEmpty(token))
                return token;
        }
        catch
        {
            // best effort
        }
        return null;
    }

    private static void RemoveStoredToken()
    {
        try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }
    }
}
