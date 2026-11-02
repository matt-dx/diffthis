using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DiffThis.Core.Services;

/// <summary>
/// Authenticates with Bitbucket Cloud two ways: an Atlassian account email + API token pasted
/// in directly, or signing in with a browser via Bitbucket's OAuth 2.0 Authorization Code flow
/// (loopback redirect — the standard native-app OAuth pattern, RFC 8252). Bitbucket Cloud has no
/// Device Flow and its OAuth consumers are registered with a fixed callback URL (not the
/// any-port loopback some providers allow), so the redirect listens on one specific port that
/// must match the consumer's configured Callback URL exactly.
///
/// Unlike GitHub's Client ID or GitLab's Application ID (both meant to be public — no secret
/// involved), Bitbucket's OAuth client has a genuine Secret, so it's never committed to source.
/// Instead, CI embeds it into release builds from a GitHub Actions secret, written to a
/// gitignored <c>bitbucket-oauth-secrets.json</c> right before compiling (a developer can do the
/// same locally from the matching <c>.example.json</c>) — see <see cref="LoadBuiltInSecrets"/>.
/// A fresh clone with no such file still builds fine; it just falls back to requiring the user's
/// own Key/Secret under Settings → Source Control → Bitbucket.
///
/// The API-token sign-in does not probe any Bitbucket endpoint: a user-scoped API token's
/// "Repositories" / "Pull requests" scopes only grant access to endpoints scoped to a single,
/// named repository (exactly what <see cref="IPullRequestService"/> calls), not to any account-
/// or cross-workspace "list my repositories" endpoint — those need the separate
/// read:user:bitbucket / read:workspace:bitbucket scopes we never ask for, and return
/// 401/403/404 depending on the endpoint even for a perfectly valid token. Bad credentials are
/// instead caught the first time a real PR fetch is attempted, surfacing the sign-in modal again.
/// </summary>
public class BitbucketAuthService : IBitbucketAuthService
{
    private const int CallbackPort = 51738; // must match the consumer's Callback URL exactly

    private static readonly Lazy<BuiltInSecrets?> BuiltInSecretsLazy = new(LoadBuiltInSecrets);

    private const string AuthorizeUrl = "https://bitbucket.org/site/oauth2/authorize";
    private const string TokenUrl     = "https://bitbucket.org/site/oauth2/access_token";

    // No `scope` parameter is sent: Bitbucket's newer "OAuth clients" UI configures scopes
    // (e.g. "Repositories: Read", "Pull requests: Read") directly on the client using its own
    // granular scope identifiers, which aren't the legacy `repository`/`pullrequest` OAuth scope
    // strings this flow would otherwise request. Omitting `scope` grants whatever the client
    // itself is configured with, sidestepping the naming mismatch entirely.

    private const string AppPasswordKey = "bitbucket_app_password";
    private const string OAuthTokenKey  = "bitbucket_oauth_tokens";

    private readonly ISettingsService _settings;
    private readonly HttpClient       _http = new();

    public BitbucketAuthState State     { get; private set; } = BitbucketAuthState.NotFound;
    public Task InitializationTask { get; }
    public string?            Username  { get; private set; }
    public string?            LastError { get; private set; }

    public event Action? StateChanged;

    public BitbucketAuthService(ISettingsService settings)
    {
        _settings = settings;
        Username  = string.IsNullOrEmpty(settings.BitbucketUsername) ? null : settings.BitbucketUsername;
        InitializationTask = InitializeStateAsync();
    }

    public async Task<AuthenticationHeaderValue?> GetAuthHeaderAsync()
    {
        var oauth = await GetValidOAuthTokensAsync();
        if (oauth is not null)
            return new AuthenticationHeaderValue("Bearer", oauth.AccessToken);

        var creds = await GetCredentialsAsync();
        return creds is null
            ? null
            : new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Value.Username}:{creds.Value.AppPassword}")));
    }

    // ── API token sign-in ────────────────────────────────────────────────────

    public async Task<bool> SignInAsync(string username, string appPassword, CancellationToken ct = default)
    {
        username    = username.Trim();
        appPassword = appPassword.Trim();

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(appPassword))
        {
            LastError = "Enter both your Atlassian account email and an API token.";
            StateChanged?.Invoke();
            return false;
        }

        try
        {
            if (!await TryStoreAsync(AppPasswordKey, appPassword))
            {
                LastError = "Unable to securely store Bitbucket credentials on this device.";
                StateChanged?.Invoke();
                return false;
            }

            // GetAuthHeaderAsync prefers an unexpired OAuth token over these credentials — without
            // clearing it, signing in here after a rejected OAuth token keeps sending that same
            // rejected token instead of the one just entered.
            try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }

            _settings.BitbucketUsername = username;
            Username  = username;
            State     = BitbucketAuthState.Authenticated;
            LastError = null;
            StateChanged?.Invoke();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = $"Bitbucket sign-in failed: {ex.Message}";
            StateChanged?.Invoke();
            return false;
        }
    }

    // ── Browser (OAuth) sign-in ──────────────────────────────────────────────

    public async Task<bool> SignInWithBrowserAsync(CancellationToken ct = default)
    {
        var (clientId, clientSecret) = await ResolveOAuthClientAsync();
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            LastError = "Add a Bitbucket OAuth client Key and Secret under Settings first.";
            StateChanged?.Invoke();
            return false;
        }

        HttpListener? listener = null;
        try
        {
            // HttpListener prefix matching is strict about trailing slashes — a prefix of
            // "/callback/" never matches a request for "/callback" (no trailing slash), which is
            // exactly what the OAuth redirect below sends. Listen at the root instead; the
            // redirect URI still must stay "http://localhost:{port}/callback" to match the
            // consumer's registered Callback URL exactly.
            var (listenerStarted, port) = OAuthLoopback.StartListener([CallbackPort], "/");
            listener = listenerStarted;
            var redirectUri = $"http://localhost:{port}/callback";

            var (verifier, challenge) = OAuthLoopback.CreatePkcePair();
            var state = OAuthLoopback.CreateState();

            var authorizeUrl = $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId)}" +
                $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";

            OAuthLoopback.OpenBrowser(authorizeUrl);

            var (code, returnedState) = await OAuthLoopback.WaitForRedirectAsync(listener, TimeSpan.FromMinutes(5), ct);
            listener = null; // WaitForRedirectAsync already stopped/closed it
            if (returnedState != state)
                throw new InvalidOperationException("Sign-in response did not match the request (possible interference). Please try again.");

            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"]    = "authorization_code",
                    ["code"]          = code,
                    ["redirect_uri"]  = redirectUri,
                    ["code_verifier"] = verifier,
                }),
            };
            tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

            var tokenResp = await _http.SendAsync(tokenReq, ct);
            var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
            if (!tokenResp.IsSuccessStatusCode)
            {
                LastError = ExtractOAuthError(tokenBody) is { Length: > 0 } msg
                    ? $"Bitbucket sign-in failed: {msg}"
                    : $"Bitbucket sign-in failed: HTTP {(int)tokenResp.StatusCode}";
                StateChanged?.Invoke();
                return false;
            }

            var tokens = ParseTokenResponse(tokenBody);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                LastError = "Bitbucket sign-in failed: no access token was returned.";
                StateChanged?.Invoke();
                return false;
            }

            if (!await TryStoreAsync(OAuthTokenKey, JsonSerializer.Serialize(tokens)))
            {
                LastError = "Unable to securely store the Bitbucket token on this device.";
                StateChanged?.Invoke();
                return false;
            }

            await TryReadUsernameAsync(tokens.AccessToken, ct);

            State     = BitbucketAuthState.Authenticated;
            LastError = null;
            StateChanged?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "Sign-in was cancelled or timed out.";
            StateChanged?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Bitbucket sign-in failed: {ex.Message}";
            StateChanged?.Invoke();
            return false;
        }
        finally
        {
            try { listener?.Stop(); listener?.Close(); } catch { /* best effort */ }
        }
    }

    private async Task TryReadUsernameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bitbucket.org/2.0/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Accept", "application/json");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Username = doc.RootElement.TryGetProperty("display_name", out var name) ? name.GetString()
                : doc.RootElement.TryGetProperty("username", out var uname) ? uname.GetString() : null;
        }
        catch { /* best effort — the account-read scope needed here isn't one we require */ }
    }

    private async Task<OAuthTokens?> GetValidOAuthTokensAsync()
    {
        var stored = await GetStoredAsync(OAuthTokenKey);
        if (string.IsNullOrEmpty(stored)) return null;

        OAuthTokens? tokens;
        try { tokens = JsonSerializer.Deserialize<OAuthTokens>(stored); }
        catch { return null; }
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken)) return null;

        if (DateTimeOffset.UtcNow < DateTimeOffset.FromUnixTimeMilliseconds(tokens.ExpiresAt) - TimeSpan.FromMinutes(2))
            return tokens;

        if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

        var (clientId, clientSecret) = await ResolveOAuthClientAsync();
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"]    = "refresh_token",
                    ["refresh_token"] = tokens.RefreshToken,
                }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var refreshed = ParseTokenResponse(await resp.Content.ReadAsStringAsync());
            if (refreshed is null || string.IsNullOrEmpty(refreshed.AccessToken)) return null;

            // Bitbucket's refresh response omits refresh_token when it hasn't rotated it.
            refreshed.RefreshToken ??= tokens.RefreshToken;

            await TryStoreAsync(OAuthTokenKey, JsonSerializer.Serialize(refreshed));
            return refreshed;
        }
        catch { return null; }
    }

    private static OAuthTokens? ParseTokenResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken  = root.GetProperty("access_token").GetString() ?? "";
            var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var expiresIn    = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            return new OAuthTokens
            {
                AccessToken  = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt    = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds(),
            };
        }
        catch { return null; }
    }

    private static string? ExtractOAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error_description", out var desc) ? desc.GetString()
                : doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<bool> RefreshAsync()
    {
        var authenticated = await GetValidOAuthTokensAsync() is not null || await GetCredentialsAsync() is not null;
        State = authenticated ? BitbucketAuthState.Authenticated : BitbucketAuthState.NotFound;
        StateChanged?.Invoke();
        return authenticated;
    }

    public void SignOut()
    {
        try { SecureStorage.Default.Remove(AppPasswordKey); } catch { /* best effort */ }
        try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }
        _settings.BitbucketUsername = "";

        Username  = null;
        State     = BitbucketAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    private async Task InitializeStateAsync()
    {
        var authenticated = await GetValidOAuthTokensAsync() is not null || await GetCredentialsAsync() is not null;
        if (authenticated)
        {
            State = BitbucketAuthState.Authenticated;
            StateChanged?.Invoke();
        }
    }

    private async Task<(string ClientId, string ClientSecret)> ResolveOAuthClientAsync()
    {
        var clientId     = _settings.BitbucketOAuthClientId.Trim();
        var clientSecret = (await _settings.GetBitbucketOAuthClientSecretAsync()).Trim();
        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            return (clientId, clientSecret);

        var builtIn = BuiltInSecretsLazy.Value;
        return builtIn is null ? ("", "") : (builtIn.ClientId, builtIn.ClientSecret);
    }

    // Reads the embedded bitbucket-oauth-secrets.json (see the class remarks above) — absent on
    // a fresh clone, present when CI or a developer materialized it before building.
    private static BuiltInSecrets? LoadBuiltInSecrets()
    {
        try
        {
            using var stream = typeof(BitbucketAuthService).Assembly
                .GetManifestResourceStream("DiffThis.Core.Services.bitbucket-oauth-secrets.json");
            if (stream is null) return null;

            var secrets = JsonSerializer.Deserialize<BuiltInSecrets>(stream);
            return secrets is { ClientId.Length: > 0, ClientSecret.Length: > 0 } ? secrets : null;
        }
        catch { return null; }
    }

    private async Task<(string Username, string AppPassword)?> GetCredentialsAsync()
    {
        var username = _settings.BitbucketUsername;
        if (string.IsNullOrEmpty(username)) return null;

        var appPassword = await GetStoredAsync(AppPasswordKey);
        return string.IsNullOrEmpty(appPassword) ? null : (username, appPassword);
    }

    private static async Task<bool> TryStoreAsync(string key, string value)
    {
        try { await SecureStorage.Default.SetAsync(key, value); return true; }
        catch { return false; }
    }

    private static async Task<string?> GetStoredAsync(string key)
    {
        try
        {
            var value = await SecureStorage.Default.GetAsync(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch { return null; }
    }

    private sealed class OAuthTokens
    {
        public string  AccessToken  { get; set; } = "";
        public string? RefreshToken { get; set; }
        public long    ExpiresAt    { get; set; } // Unix ms
    }

    private sealed class BuiltInSecrets
    {
        public string ClientId     { get; set; } = "";
        public string ClientSecret { get; set; } = "";
    }
}
