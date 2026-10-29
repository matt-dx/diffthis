using System.Net.Http.Headers;

namespace DiffThis.Core.Services;

public enum AzureDevOpsAuthState { NotFound, Authenticated }

public interface IAzureDevOpsAuthService
{
    AzureDevOpsAuthState State     { get; }
    string?              Username  { get; }
    string?              LastError { get; }

    event Action? StateChanged;

    /// Returns the Authorization header to send on Azure DevOps REST calls — Bearer for a
    /// browser (OAuth) sign-in, refreshing it first if it's close to expiry, or Basic for a
    /// pasted Personal Access Token — or null if not connected.
    Task<AuthenticationHeaderValue?> GetAuthHeaderAsync();

    /// Validates the given Personal Access Token against Azure DevOps and, on success, stores it.
    /// Returns false (with <see cref="LastError"/> set) on failure.
    Task<bool> SignInAsync(string personalAccessToken, CancellationToken ct = default);

    /// Signs in via the system browser using Microsoft Entra ID (Authorization Code + PKCE,
    /// loopback redirect — no client secret). Only works for organizations backed by a
    /// Microsoft Entra (work/school) tenant, not a personal Microsoft account.
    Task<bool> SignInWithBrowserAsync(CancellationToken ct = default);

    Task<bool> RefreshAsync();
    void SignOut();
}
