using System.Net.Http.Headers;

namespace DiffThis.Core.Services;

public enum BitbucketAuthState { NotFound, Authenticated }

public interface IBitbucketAuthService
{
    BitbucketAuthState State     { get; }
    string?            Username  { get; }
    string?            LastError { get; }

    event Action? StateChanged;

    /// Completes once startup's stored-credential check has run and <see cref="State"/> reflects
    /// it — await before reading <see cref="State"/> right after construction (e.g. on page load)
    /// to avoid racing the async SecureStorage read.
    Task InitializationTask { get; }

    /// Returns the Authorization header to send on Bitbucket REST calls — Bearer for a browser
    /// (OAuth) sign-in, refreshing it first if it's close to expiry, or Basic for an email +
    /// API token pair — or null if not connected.
    Task<AuthenticationHeaderValue?> GetAuthHeaderAsync();

    /// Stores the given Atlassian account email + API token. Does not call the Bitbucket API —
    /// see <see cref="BitbucketAuthService"/> for why — so a bad pair is only caught on the
    /// first real pull-request fetch. Returns false (with <see cref="LastError"/> set) only for
    /// local failures (empty fields, unable to write to secure storage).
    Task<bool> SignInAsync(string username, string appPassword, CancellationToken ct = default);

    /// Signs in via the system browser (Authorization Code + PKCE, loopback redirect).
    Task<bool> SignInWithBrowserAsync(CancellationToken ct = default);

    Task<bool> RefreshAsync();
    void SignOut();
}
