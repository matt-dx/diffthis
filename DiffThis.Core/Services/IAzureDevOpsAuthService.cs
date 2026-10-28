namespace DiffThis.Core.Services;

public enum AzureDevOpsAuthState { NotFound, Authenticated }

public interface IAzureDevOpsAuthService
{
    AzureDevOpsAuthState State     { get; }
    string?              Username  { get; }
    string?              LastError { get; }

    event Action? StateChanged;

    /// Returns the stored Personal Access Token, or null if not connected.
    Task<string?> GetTokenAsync();

    /// Validates the given Personal Access Token against Azure DevOps and, on success, stores it.
    /// Returns false (with <see cref="LastError"/> set) on failure.
    Task<bool> SignInAsync(string personalAccessToken, CancellationToken ct = default);

    Task<bool> RefreshAsync();
    void SignOut();
}
