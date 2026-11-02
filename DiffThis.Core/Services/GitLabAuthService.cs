using System.Text.Json;

namespace DiffThis.Core.Services;

/// <summary>
/// Authenticates with GitLab.com via the standard OAuth Device Flow. DiffThis ships with its
/// own registered Application's ID (public by design, like GitHub's) so sign-in works out of
/// the box; Settings still lets a user override it with their own Application's ID. Mirrors
/// <see cref="GitHubAuthService"/>.
/// </summary>
public class GitLabAuthService : IGitLabAuthService
{
    private const string DefaultClientId = "23be9dfb929aab1c9f4075bf14cceaa174c3459ff79a7f6b112401a8d370ea69";

    private const string OAuthTokenKey = "gitlab_oauth_token";
    private const string UsernameKey   = "gitlab_username";

    // Sentinel "never expires" timestamp for tokens stored before refresh-token support was
    // added (see GetStoredTokensAsync) — the max value DateTimeOffset.FromUnixTimeMilliseconds
    // accepts, since long.MaxValue itself overflows that range.
    private static readonly long NeverExpiresUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private const string DeviceCodeUrl  = "https://gitlab.com/oauth/authorize_device";
    private const string AccessTokenUrl = "https://gitlab.com/oauth/token";

    private readonly ISettingsService _settings;
    private readonly HttpClient       _http = new();

    private CancellationTokenSource? _pollCts;

    // GitLab rotates refresh tokens — using one invalidates it. Two overlapping callers (e.g. the
    // Open and Closed PR tabs loading at once) reading the same expired token set before either
    // has persisted a refresh would both submit the same refresh_token; the second submission is
    // then rejected as already-used even though the first one just succeeded. Serializing the
    // whole read/refresh/persist sequence, and re-reading the stored tokens after acquiring the
    // gate, lets a caller that waited pick up the refresh the first caller already did.
    private readonly SemaphoreSlim _tokenRefreshGate = new(1, 1);

    public GitLabAuthState State     { get; private set; } = GitLabAuthState.NotFound;
    public Task InitializationTask { get; }
    public string?         Username  { get; private set; }
    public string?         LastError { get; private set; }

    public event Action? StateChanged;

    public GitLabAuthService(ISettingsService settings)
    {
        _settings = settings;
        Username  = Preferences.Get(UsernameKey, (string?)null);
        InitializationTask = InitializeStateAsync();
    }

    public async Task<string?> GetTokenAsync() => (await GetValidTokensAsync())?.AccessToken;

    public async Task<bool> RefreshAsync()
    {
        var stored = await GetValidTokensAsync();
        State = stored is null ? GitLabAuthState.NotFound : GitLabAuthState.Authenticated;
        StateChanged?.Invoke();
        return State == GitLabAuthState.Authenticated;
    }

    public async Task<GitLabDeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        var clientId = _settings.GitLabOAuthClientId.Trim();
        if (string.IsNullOrEmpty(clientId)) clientId = DefaultClientId;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"]     = "read_api",
            });
            req.Headers.Add("Accept", "application/json");

            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errProp))
            {
                LastError = root.TryGetProperty("error_description", out var desc)
                    ? desc.GetString() : errProp.GetString();
                State = GitLabAuthState.NotFound;
                StateChanged?.Invoke();
                return null;
            }

            var deviceCode = root.GetProperty("device_code").GetString()!;
            var userCode   = root.GetProperty("user_code").GetString()!;
            var verifyUri  = root.GetProperty("verification_uri").GetString()!;
            var expiresIn  = root.GetProperty("expires_in").GetInt32();
            var interval   = root.GetProperty("interval").GetInt32();

            State     = GitLabAuthState.PendingDeviceFlow;
            LastError = null;
            StateChanged?.Invoke();

            _pollCts = new CancellationTokenSource();
            _ = PollForTokenAsync(clientId, deviceCode, interval, expiresIn, _pollCts.Token);

            return new GitLabDeviceFlowInfo(userCode, verifyUri, expiresIn, interval);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            State     = GitLabAuthState.NotFound;
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
                req.Headers.Add("Accept", "application/json");

                var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // GitLab returns 400 for "authorization_pending" — inspect the body before giving up
                    var pendingBody = await resp.Content.ReadAsStringAsync(ct);
                    string? errorCode = null;
                    try
                    {
                        using var pendingDoc = JsonDocument.Parse(pendingBody);
                        errorCode = pendingDoc.RootElement.TryGetProperty("error", out var pendingErr) ? pendingErr.GetString() : null;
                    }
                    catch { /* couldn't parse the body — keep polling, let the deadline time it out */ }

                    if (errorCode is "authorization_pending" or null) continue;
                    if (errorCode == "slow_down") { interval += 5; continue; }

                    // Any other error is terminal (access_denied, expired_token, ...) — stop polling
                    // now instead of leaving the user on "Waiting for authorization" until expiresIn
                    // elapses and reporting an inaccurate timeout.
                    State     = GitLabAuthState.NotFound;
                    LastError = errorCode switch
                    {
                        "access_denied" => "Sign-in was denied.",
                        "expired_token" => "The sign-in code expired. Please try again.",
                        _               => $"GitLab sign-in failed: {errorCode}",
                    };
                    StateChanged?.Invoke();
                    return;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                var tokens = ParseTokenResponse(body);
                if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken)) continue;

                if (!await TryStoreTokensAsync(tokens))
                {
                    State     = GitLabAuthState.NotFound;
                    LastError = "Unable to securely store GitLab credentials on this device.";
                    StateChanged?.Invoke();
                    return;
                }

                State     = GitLabAuthState.Authenticated;
                LastError = null;
                StateChanged?.Invoke();

                _ = TryReadUsernameAsync(tokens.AccessToken);
                return;
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep polling on transient errors */ }
        }

        if (State == GitLabAuthState.PendingDeviceFlow)
        {
            State     = GitLabAuthState.NotFound;
            LastError = ct.IsCancellationRequested
                ? "Sign-in was cancelled."
                : "Sign-in timed out. Please try again.";
            StateChanged?.Invoke();
        }
    }

    public void SignOut()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        RemoveStoredToken();
        Preferences.Remove(UsernameKey);

        Username  = null;
        State     = GitLabAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    private async Task TryReadUsernameAsync(string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://gitlab.com/api/v4/user");
            req.Headers.Add("Authorization", $"Bearer {token}");

            var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("username", out var username)) return;

            Username = username.GetString();
            Preferences.Set(UsernameKey, Username ?? "");
            StateChanged?.Invoke();
        }
        catch { /* best effort */ }
    }

    private async Task InitializeStateAsync()
    {
        try
        {
            var stored = await GetValidTokensAsync();
            if (stored is not null)
            {
                State = GitLabAuthState.Authenticated;
                StateChanged?.Invoke();
            }
        }
        catch { /* best effort */ }
    }

    // GitLab's OAuth access tokens expire (the device flow response includes a refresh_token and
    // expires_in just like a normal Authorization Code flow) — reread the stored token set here
    // and refresh it before it's handed to an API caller, instead of only ever storing the bare
    // access_token and leaving a successful sign-in to silently stop working after expiry.
    private async Task<OAuthTokens?> GetValidTokensAsync()
    {
        await _tokenRefreshGate.WaitAsync();
        try
        {
            var stored = await GetStoredTokensAsync();
            if (stored is null || string.IsNullOrEmpty(stored.AccessToken)) return null;

            if (DateTimeOffset.UtcNow < DateTimeOffset.FromUnixTimeMilliseconds(stored.ExpiresAt) - TimeSpan.FromMinutes(2))
                return stored;

            if (string.IsNullOrEmpty(stored.RefreshToken)) return null;

            var clientId = _settings.GitLabOAuthClientId.Trim();
            if (string.IsNullOrEmpty(clientId)) clientId = DefaultClientId;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"]    = "refresh_token",
                        ["refresh_token"] = stored.RefreshToken,
                        ["client_id"]     = clientId,
                    }),
                };
                req.Headers.Add("Accept", "application/json");

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var refreshed = ParseTokenResponse(await resp.Content.ReadAsStringAsync());
                if (refreshed is null || string.IsNullOrEmpty(refreshed.AccessToken)) return null;

                refreshed.RefreshToken ??= stored.RefreshToken;
                await TryStoreTokensAsync(refreshed);
                return refreshed;
            }
            catch { return null; }
        }
        finally { _tokenRefreshGate.Release(); }
    }

    private static OAuthTokens? ParseTokenResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return null;

            var accessToken  = root.GetProperty("access_token").GetString() ?? "";
            var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var expiresIn    = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 7200;
            return new OAuthTokens
            {
                AccessToken  = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt    = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds(),
            };
        }
        catch { return null; }
    }

    private static async Task<bool> TryStoreTokensAsync(OAuthTokens tokens)
    {
        try { await SecureStorage.Default.SetAsync(OAuthTokenKey, JsonSerializer.Serialize(tokens)); return true; }
        catch { return false; }
    }

    private static async Task<OAuthTokens?> GetStoredTokensAsync()
    {
        string? stored;
        try { stored = await SecureStorage.Default.GetAsync(OAuthTokenKey); }
        catch { return null; }

        if (string.IsNullOrEmpty(stored)) return null;

        try
        {
            return JsonSerializer.Deserialize<OAuthTokens>(stored);
        }
        catch (JsonException)
        {
            // Sign-ins from before refresh-token support stored the bare access_token string.
            // Treat it as a still-valid, non-expiring token instead of silently signing the user
            // out — a real 401 from a later API call still surfaces the usual re-sign-in prompt.
            var legacy = new OAuthTokens { AccessToken = stored, ExpiresAt = NeverExpiresUnixMs };
            await TryStoreTokensAsync(legacy);
            return legacy;
        }
    }

    private static void RemoveStoredToken()
    {
        try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }
    }

    private sealed class OAuthTokens
    {
        public string  AccessToken  { get; set; } = "";
        public string? RefreshToken { get; set; }
        public long    ExpiresAt    { get; set; } // Unix ms
    }
}
