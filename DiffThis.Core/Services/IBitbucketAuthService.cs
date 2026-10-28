namespace DiffThis.Core.Services;

public enum BitbucketAuthState { NotFound, Authenticated }

public interface IBitbucketAuthService
{
    BitbucketAuthState State     { get; }
    string?            Username  { get; }
    string?            LastError { get; }

    event Action? StateChanged;

    /// Returns the stored (email, API token) pair, or null if not connected.
    Task<(string Username, string AppPassword)?> GetCredentialsAsync();

    /// Stores the given Atlassian account email + API token. Does not call the Bitbucket API —
    /// see <see cref="BitbucketAuthService"/> for why — so a bad pair is only caught on the
    /// first real pull-request fetch. Returns false (with <see cref="LastError"/> set) only for
    /// local failures (empty fields, unable to write to secure storage).
    Task<bool> SignInAsync(string username, string appPassword, CancellationToken ct = default);

    Task<bool> RefreshAsync();
    void SignOut();
}
