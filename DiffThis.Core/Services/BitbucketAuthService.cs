namespace DiffThis.Core.Services;

/// <summary>
/// Authenticates with Bitbucket Cloud using an Atlassian account email + API token — the
/// closest Bitbucket Cloud has to a device flow (it supports neither OAuth Device Flow nor any
/// unattended flow without registering a web OAuth consumer and standing up a redirect
/// listener). Bitbucket App Passwords were retired in favor of Atlassian API tokens with
/// scopes; the token is created entirely in-app from the user's perspective: a link to
/// Atlassian's own account settings page, paste the result back here, no terminal involved.
///
/// Sign-in does not probe any Bitbucket endpoint: a user-scoped API token's "Repositories" /
/// "Pull requests" scopes only grant access to endpoints scoped to a single, named repository
/// (exactly what <see cref="IPullRequestService"/> calls), not to any account- or
/// cross-workspace "list my repositories" endpoint — those need the separate
/// read:user:bitbucket / read:workspace:bitbucket scopes we never ask for, and return
/// 401/403/404 depending on the endpoint even for a perfectly valid token. Bad credentials are
/// instead caught the first time a real PR fetch is attempted, surfacing the sign-in modal again.
/// </summary>
public class BitbucketAuthService : IBitbucketAuthService
{
    private const string AppPasswordKey = "bitbucket_app_password";

    private readonly ISettingsService _settings;

    public BitbucketAuthState State     { get; private set; } = BitbucketAuthState.NotFound;
    public string?            Username  { get; private set; }
    public string?            LastError { get; private set; }

    public event Action? StateChanged;

    public BitbucketAuthService(ISettingsService settings)
    {
        _settings = settings;
        Username  = string.IsNullOrEmpty(settings.BitbucketUsername) ? null : settings.BitbucketUsername;
        _ = InitializeStateAsync();
    }

    public async Task<(string Username, string AppPassword)?> GetCredentialsAsync()
    {
        var username = _settings.BitbucketUsername;
        if (string.IsNullOrEmpty(username)) return null;

        var appPassword = await GetStoredAppPasswordAsync();
        return string.IsNullOrEmpty(appPassword) ? null : (username, appPassword);
    }

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
            if (!await TryStoreAppPasswordAsync(appPassword))
            {
                LastError = "Unable to securely store Bitbucket credentials on this device.";
                StateChanged?.Invoke();
                return false;
            }

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

    public async Task<bool> RefreshAsync()
    {
        var creds = await GetCredentialsAsync();
        State = creds is null ? BitbucketAuthState.NotFound : BitbucketAuthState.Authenticated;
        StateChanged?.Invoke();
        return State == BitbucketAuthState.Authenticated;
    }

    public void SignOut()
    {
        try { SecureStorage.Default.Remove(AppPasswordKey); } catch { /* best effort */ }
        _settings.BitbucketUsername = "";

        Username  = null;
        State     = BitbucketAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    private async Task InitializeStateAsync()
    {
        var creds = await GetCredentialsAsync();
        if (creds is not null)
        {
            State = BitbucketAuthState.Authenticated;
            StateChanged?.Invoke();
        }
    }

    private static async Task<bool> TryStoreAppPasswordAsync(string appPassword)
    {
        try { await SecureStorage.Default.SetAsync(AppPasswordKey, appPassword); return true; }
        catch { return false; }
    }

    private static async Task<string?> GetStoredAppPasswordAsync()
    {
        try
        {
            var value = await SecureStorage.Default.GetAsync(AppPasswordKey);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch { return null; }
    }
}
