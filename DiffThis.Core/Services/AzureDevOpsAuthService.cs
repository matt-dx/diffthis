using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DiffThis.Core.Services;

/// <summary>
/// Authenticates with Azure DevOps two ways: a Personal Access Token pasted in directly, or
/// signing in with a browser via Microsoft Entra ID (Authorization Code + PKCE, loopback
/// redirect — the standard native-app OAuth pattern, RFC 8252). DiffThis ships with its own
/// registered Entra app registration's Client ID (public by design, like the GitHub/GitLab
/// ones) so browser sign-in works out of the box; Settings can override it. Entra apps don't
/// support personal Microsoft accounts for Azure DevOps, so this only works for organizations
/// backed by a Microsoft Entra (work/school) tenant — PAT sign-in remains for anyone else.
/// </summary>
public class AzureDevOpsAuthService : IAzureDevOpsAuthService
{
    private const string DefaultClientId = "218fda64-6496-4af2-a8fd-b11bb98e1ed4"; // reuses the app's existing Microsoft Store/Partner Center Entra registration

    private const string AuthorizeUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize";
    private const string TokenUrl     = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token";
    private const string Scope        = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";

    private const string TokenKey      = "azuredevops_pat";
    private const string OAuthTokenKey = "azuredevops_oauth_tokens";

    private readonly ISettingsService _settings;
    private readonly HttpClient       _http = new();

    public AzureDevOpsAuthState State     { get; private set; } = AzureDevOpsAuthState.NotFound;
    public Task InitializationTask { get; }
    public string?              Username  { get; private set; }
    public string?              LastError { get; private set; }

    public event Action? StateChanged;

    public AzureDevOpsAuthService(ISettingsService settings)
    {
        _settings = settings;
        InitializationTask = InitializeStateAsync();
    }

    public async Task<AuthenticationHeaderValue?> GetAuthHeaderAsync()
    {
        var oauth = await GetValidOAuthTokensAsync();
        if (oauth is not null)
            return new AuthenticationHeaderValue("Bearer", oauth.AccessToken);

        var pat = await GetStoredPatAsync();
        return string.IsNullOrEmpty(pat)
            ? null
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}")));
    }

    // ── Personal Access Token sign-in ───────────────────────────────────────

    public async Task<bool> SignInAsync(string personalAccessToken, CancellationToken ct = default)
    {
        personalAccessToken = personalAccessToken.Trim();
        if (string.IsNullOrEmpty(personalAccessToken))
        {
            LastError = "Enter a Personal Access Token.";
            StateChanged?.Invoke();
            return false;
        }

        try
        {
            if (!await TryStoreAsync(TokenKey, personalAccessToken))
            {
                LastError = "Unable to securely store the Azure DevOps token on this device.";
                StateChanged?.Invoke();
                return false;
            }

            // GetAuthHeaderAsync prefers an unexpired OAuth token over the PAT — without clearing
            // it, signing in here after a rejected OAuth token keeps sending that rejected token.
            try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }

            // The profile endpoint this used to validate against needs profile-read access, which
            // a Code-scoped PAT (what the sign-in form asks users to create) doesn't grant — that
            // rejected otherwise-valid tokens outright. Mirror Bitbucket's token flow: don't
            // validate upfront, just read the display name best-effort, and let a real PR request
            // surface bad credentials.
            await TryReadUsernameFromPatAsync(personalAccessToken, ct);

            State     = AzureDevOpsAuthState.Authenticated;
            LastError = null;
            StateChanged?.Invoke();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = $"Azure DevOps sign-in failed: {ex.Message}";
            StateChanged?.Invoke();
            return false;
        }
    }

    // ── Browser (Microsoft Entra ID) sign-in ────────────────────────────────

    public async Task<bool> SignInWithBrowserAsync(CancellationToken ct = default)
    {
        var clientId = _settings.AzureDevOpsOAuthClientId.Trim();
        if (string.IsNullOrEmpty(clientId)) clientId = DefaultClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            LastError = "Add an Azure DevOps (Entra ID) Client ID under Settings first.";
            StateChanged?.Invoke();
            return false;
        }

        HttpListener? listener = null;
        try
        {
            // Entra's any-port leniency for a registered bare "http://localhost" only tolerates a
            // different port, not a different path — unlike Bitbucket's OAuth client (which is
            // registered with a fixed port AND a "/callback" path), this redirect URI must carry
            // no path at all.
            var (listenerStarted, port) = OAuthLoopback.StartListener(OAuthLoopback.RandomEphemeralPorts(), "/");
            listener = listenerStarted;
            var redirectUri = $"http://localhost:{port}";

            var (verifier, challenge) = OAuthLoopback.CreatePkcePair();
            var state = OAuthLoopback.CreateState();

            var authorizeUrl = $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId)}" +
                $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_mode=query&scope={Uri.EscapeDataString(Scope)}" +
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
                    ["client_id"]     = clientId,
                    ["code"]          = code,
                    ["redirect_uri"]  = redirectUri,
                    ["code_verifier"] = verifier,
                    ["scope"]         = Scope,
                }),
            };

            var tokenResp = await _http.SendAsync(tokenReq, ct);
            var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
            if (!tokenResp.IsSuccessStatusCode)
            {
                LastError = ExtractOAuthError(tokenBody) is { Length: > 0 } msg
                    ? $"Azure DevOps sign-in failed: {msg}"
                    : $"Azure DevOps sign-in failed: HTTP {(int)tokenResp.StatusCode}";
                StateChanged?.Invoke();
                return false;
            }

            var tokens = ParseTokenResponse(tokenBody);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                LastError = "Azure DevOps sign-in failed: no access token was returned.";
                StateChanged?.Invoke();
                return false;
            }

            if (!await TryStoreAsync(OAuthTokenKey, JsonSerializer.Serialize(tokens)))
            {
                LastError = "Unable to securely store the Azure DevOps token on this device.";
                StateChanged?.Invoke();
                return false;
            }

            await TryReadUsernameAsync(tokens.AccessToken, ct);

            State     = AzureDevOpsAuthState.Authenticated;
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
            LastError = $"Azure DevOps sign-in failed: {ex.Message}";
            StateChanged?.Invoke();
            return false;
        }
        finally
        {
            try { listener?.Stop(); listener?.Close(); } catch { /* best effort */ }
        }
    }

    private async Task TryReadUsernameFromPatAsync(string pat, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}")));

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Username = doc.RootElement.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        }
        catch { /* best effort — a Code-scoped PAT may not grant profile-read access */ }
    }

    private async Task TryReadUsernameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Username = doc.RootElement.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        }
        catch { /* best effort */ }
    }

    private async Task<OAuthTokens?> GetValidOAuthTokensAsync()
    {
        var stored = await GetStoredAsync(OAuthTokenKey);
        if (string.IsNullOrEmpty(stored)) return null;

        OAuthTokens? tokens;
        try { tokens = JsonSerializer.Deserialize<OAuthTokens>(stored); }
        catch { return null; }
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken)) return null;

        // Refresh a bit before actual expiry so an API call never races a token that's about to die.
        if (DateTimeOffset.UtcNow < DateTimeOffset.FromUnixTimeMilliseconds(tokens.ExpiresAt) - TimeSpan.FromMinutes(2))
            return tokens;

        if (string.IsNullOrEmpty(tokens.RefreshToken)) return null;

        var clientId = _settings.AzureDevOpsOAuthClientId.Trim();
        if (string.IsNullOrEmpty(clientId)) clientId = DefaultClientId;
        if (string.IsNullOrEmpty(clientId)) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"]    = "refresh_token",
                    ["client_id"]     = clientId,
                    ["refresh_token"] = tokens.RefreshToken,
                    ["scope"]         = Scope,
                }),
            };

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var refreshed = ParseTokenResponse(await resp.Content.ReadAsStringAsync());
            if (refreshed is null || string.IsNullOrEmpty(refreshed.AccessToken)) return null;

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
            return doc.RootElement.TryGetProperty("error_description", out var desc) ? desc.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<bool> RefreshAsync()
    {
        var authenticated = await GetValidOAuthTokensAsync() is not null || !string.IsNullOrEmpty(await GetStoredPatAsync());
        State = authenticated ? AzureDevOpsAuthState.Authenticated : AzureDevOpsAuthState.NotFound;
        StateChanged?.Invoke();
        return authenticated;
    }

    public void SignOut()
    {
        try { SecureStorage.Default.Remove(TokenKey); } catch { /* best effort */ }
        try { SecureStorage.Default.Remove(OAuthTokenKey); } catch { /* best effort */ }

        Username  = null;
        State     = AzureDevOpsAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    private async Task InitializeStateAsync()
    {
        var authenticated = await GetValidOAuthTokensAsync() is not null || !string.IsNullOrEmpty(await GetStoredPatAsync());
        if (authenticated)
        {
            State = AzureDevOpsAuthState.Authenticated;
            StateChanged?.Invoke();
        }
    }

    private Task<string?> GetStoredPatAsync() => GetStoredAsync(TokenKey);

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
}
