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

    private const string DeviceCodeUrl  = "https://gitlab.com/oauth/authorize_device";
    private const string AccessTokenUrl = "https://gitlab.com/oauth/token";

    private readonly ISettingsService _settings;
    private readonly HttpClient       _http = new();

    private CancellationTokenSource? _pollCts;

    public GitLabAuthState State     { get; private set; } = GitLabAuthState.NotFound;
    public string?         Username  { get; private set; }
    public string?         LastError { get; private set; }

    public event Action? StateChanged;

    public GitLabAuthService(ISettingsService settings)
    {
        _settings = settings;
        Username  = Preferences.Get(UsernameKey, (string?)null);
        _ = InitializeStateAsync();
    }

    public Task<string?> GetTokenAsync() => GetStoredTokenAsync();

    public async Task<bool> RefreshAsync()
    {
        var stored = await GetStoredTokenAsync();
        State = string.IsNullOrEmpty(stored) ? GitLabAuthState.NotFound : GitLabAuthState.Authenticated;
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
                    try
                    {
                        using var pendingDoc = JsonDocument.Parse(pendingBody);
                        if (pendingDoc.RootElement.TryGetProperty("error", out var pendingErr)
                            && pendingErr.GetString() is "authorization_pending" or "slow_down")
                            continue;
                    }
                    catch { /* fall through to retry */ }
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("access_token", out var tokenProp)) continue;
                var token = tokenProp.GetString();
                if (string.IsNullOrEmpty(token)) continue;

                if (!await TryStoreTokenAsync(token))
                {
                    State     = GitLabAuthState.NotFound;
                    LastError = "Unable to securely store GitLab credentials on this device.";
                    StateChanged?.Invoke();
                    return;
                }

                State     = GitLabAuthState.Authenticated;
                LastError = null;
                StateChanged?.Invoke();

                _ = TryReadUsernameAsync(token);
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
            var stored = await GetStoredTokenAsync();
            if (!string.IsNullOrEmpty(stored))
            {
                State = GitLabAuthState.Authenticated;
                StateChanged?.Invoke();
            }
        }
        catch { /* best effort */ }
    }

    private static async Task<bool> TryStoreTokenAsync(string token)
    {
        try { await SecureStorage.Default.SetAsync(OAuthTokenKey, token); return true; }
        catch { return false; }
    }

    private static async Task<string?> GetStoredTokenAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(OAuthTokenKey);
            if (!string.IsNullOrEmpty(token)) return token;
        }
        catch { /* best effort */ }
        return null;
    }

    private static void RemoveStoredToken()
    {
        try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }
    }
}
