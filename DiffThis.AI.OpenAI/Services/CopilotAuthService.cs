using System.Text.Json;

namespace DiffThis.AI.OpenAI.Services;

/// <summary>
/// Authenticates with GitHub Copilot via the GitHub Device-Code OAuth flow, then
/// exchanges the resulting GitHub OAuth token for a short-lived Copilot session
/// token (via <c>api.github.com/copilot_internal/v2/token</c>).
///
/// The device flow uses client ID <c>Iv1.b507a08c87ecfe98</c> — the same OAuth
/// app used by VS Code and copilot.vim — so the resulting token is recognised by
/// the Copilot token-exchange endpoint.
/// </summary>
public class CopilotAuthService : ICopilotAuthService
{
    // The GitHub Copilot OAuth App client ID used by all official Copilot clients.
    // Tokens obtained via other clients (e.g. the `gh` CLI) are rejected by the
    // Copilot session-token exchange endpoint.
    private const string ClientId = "Iv1.b507a08c87ecfe98";

    // Preferences keys
    private const string OAuthTokenKey = "copilot_oauth_token";
    private const string UsernameKey   = "copilot_username";

    // GitHub OAuth endpoints
    private const string DeviceCodeUrl  = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    // Copilot session-token exchange
    private const string SessionTokenUrl = "https://api.github.com/copilot_internal/v2/token";

    private readonly HttpClient    _http = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Short-lived session token (in-memory; not persisted)
    private string?  _sessionToken;
    private DateTime _sessionTokenExpiry = DateTime.MinValue;

    // Active device-flow poll cancellation
    private CancellationTokenSource? _pollCts;

    public CopilotAuthState State     { get; private set; } = CopilotAuthState.NotFound;
    public string?          Username  { get; private set; }
    public string?          LastError { get; private set; }

    public event Action? StateChanged;

    public CopilotAuthService()
    {
        Username = Preferences.Get(UsernameKey, (string?)null);

        // If we have a stored OAuth token, assume authenticated until exchange proves otherwise.
        var stored = Preferences.Get(OAuthTokenKey, (string?)null);
        if (!string.IsNullOrEmpty(stored))
            State = CopilotAuthState.Authenticated;
    }

    // ── Session token ─────────────────────────────────────────────────────

    public async Task<string?> GetSessionTokenAsync(CancellationToken ct = default)
    {
        // Fast path — return cached token while still valid
        if (_sessionToken is not null && DateTime.UtcNow < _sessionTokenExpiry)
            return _sessionToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_sessionToken is not null && DateTime.UtcNow < _sessionTokenExpiry)
                return _sessionToken;

            var oauthToken = Preferences.Get(OAuthTokenKey, (string?)null);
            if (string.IsNullOrEmpty(oauthToken))
            {
                State = CopilotAuthState.NotFound;
                return null;
            }

            return await ExchangeForSessionTokenAsync(oauthToken, ct);
        }
        finally { _lock.Release(); }
    }

    private async Task<string?> ExchangeForSessionTokenAsync(string oauthToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SessionTokenUrl);
            req.Headers.Add("Authorization", $"token {oauthToken}");
            req.Headers.Add("User-Agent",    "DiffThis/1.0");
            req.Headers.Add("Accept",        "application/json");

            var resp = await _http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode is 401 or 403)
                {
                    // Token was revoked — force re-authentication
                    Preferences.Remove(OAuthTokenKey);
                    _sessionToken = null;
                    State         = CopilotAuthState.NotFound;
                    LastError     = "GitHub Copilot session expired. Please sign in again.";
                    StateChanged?.Invoke();
                }
                else
                {
                    LastError = $"Session token exchange failed: HTTP {(int)resp.StatusCode}";
                }
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("token", out var tokenProp))
            {
                LastError = "Unexpected response from Copilot token exchange.";
                return null;
            }

            _sessionToken = tokenProp.GetString();

            // Refresh ~60 s before the server-specified expiry
            _sessionTokenExpiry = root.TryGetProperty("refresh_in", out var refreshIn)
                ? DateTime.UtcNow.AddSeconds(refreshIn.GetInt32() - 60)
                : DateTime.UtcNow.AddMinutes(24);

            LastError = null;
            if (State != CopilotAuthState.Authenticated)
            {
                State = CopilotAuthState.Authenticated;
                StateChanged?.Invoke();
            }

            return _sessionToken;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = $"Session token exchange error: {ex.Message}";
            return null;
        }
    }

    // ── Device-code flow ──────────────────────────────────────────────────

    public async Task<DeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default)
    {
        // Cancel any in-progress flow
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"]     = "read:user",
            });
            req.Headers.Add("Accept",     "application/json");
            req.Headers.Add("User-Agent", "DiffThis/1.0");

            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var deviceCode = root.GetProperty("device_code").GetString()!;
            var userCode   = root.GetProperty("user_code").GetString()!;
            var verifyUri  = root.GetProperty("verification_uri").GetString()!;
            var expiresIn  = root.GetProperty("expires_in").GetInt32();
            var interval   = root.GetProperty("interval").GetInt32();

            State     = CopilotAuthState.PendingDeviceFlow;
            LastError = null;
            StateChanged?.Invoke();

            // Kick off background polling — fires StateChanged when done
            _pollCts = new CancellationTokenSource();
            _ = PollForOAuthTokenAsync(deviceCode, interval, expiresIn, _pollCts.Token);

            return new DeviceFlowInfo(userCode, verifyUri, expiresIn, interval);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            State     = CopilotAuthState.NotFound;
            LastError = $"Failed to start sign-in: {ex.Message}";
            StateChanged?.Invoke();
            return null;
        }
    }

    private async Task PollForOAuthTokenAsync(
        string deviceCode, int interval, int expiresIn, CancellationToken ct)
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
                    ["client_id"]   = ClientId,
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

                // authorization_pending / slow_down / expired_token
                if (root.TryGetProperty("error", out var errProp))
                {
                    if (errProp.GetString() == "slow_down"
                        && root.TryGetProperty("interval", out var newInt))
                        interval = newInt.GetInt32();
                    continue;
                }

                if (!root.TryGetProperty("access_token", out var tokenProp)) continue;
                var oauthToken = tokenProp.GetString();
                if (string.IsNullOrEmpty(oauthToken)) continue;

                // Success — store token and update state
                Preferences.Set(OAuthTokenKey, oauthToken);

                // Invalidate any cached session token so next call exchanges fresh
                _sessionToken       = null;
                _sessionTokenExpiry = DateTime.MinValue;

                State     = CopilotAuthState.Authenticated;
                LastError = null;
                StateChanged?.Invoke();

                // Fetch username asynchronously
                _ = TryReadUsernameAsync(oauthToken);
                return;
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep polling on transient errors */ }
        }

        // Flow expired or was cancelled — only reset if we're still pending
        if (State == CopilotAuthState.PendingDeviceFlow)
        {
            State     = CopilotAuthState.NotFound;
            LastError = ct.IsCancellationRequested
                ? "Sign-in was cancelled."
                : "Sign-in timed out. Please try again.";
            StateChanged?.Invoke();
        }
    }

    // ── Refresh / SignOut ─────────────────────────────────────────────────

    public Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        _sessionToken       = null;
        _sessionTokenExpiry = DateTime.MinValue;

        var stored = Preferences.Get(OAuthTokenKey, (string?)null);
        if (string.IsNullOrEmpty(stored))
        {
            State = CopilotAuthState.NotFound;
            StateChanged?.Invoke();
            return Task.FromResult(false);
        }

        State = CopilotAuthState.Authenticated;
        StateChanged?.Invoke();
        return Task.FromResult(true);
    }

    public void SignOut()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        _sessionToken       = null;
        _sessionTokenExpiry = DateTime.MinValue;

        Preferences.Remove(OAuthTokenKey);
        Preferences.Remove(UsernameKey);

        Username  = null;
        State     = CopilotAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    // ── Username fetch ────────────────────────────────────────────────────

    private async Task TryReadUsernameAsync(string oauthToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Add("Authorization", $"token {oauthToken}");
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
}
