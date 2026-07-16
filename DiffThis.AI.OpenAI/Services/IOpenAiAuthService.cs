namespace DiffThis.AI.OpenAI.Services;

public enum OpenAiAuthState { CliNotFound, NotAuthenticated, Authenticated }

public interface IOpenAiAuthService
{
    OpenAiAuthState State       { get; }
    string?         StatusText  { get; }
    string?         LastError   { get; }

    event Action? StateChanged;

    Task RefreshAsync(CancellationToken ct = default);
    Task<bool> TryInteractiveLoginAsync(CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
}
