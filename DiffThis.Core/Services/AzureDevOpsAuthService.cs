using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DiffThis.Core.Services;

/// <summary>
/// Authenticates with Azure DevOps using a Personal Access Token. Azure DevOps supports OAuth,
/// but only via Microsoft Entra ID app registration plus a redirect URI — a Personal Access
/// Token is the standard, fully in-app alternative (create it on dev.azure.com, paste it here).
/// </summary>
public class AzureDevOpsAuthService : IAzureDevOpsAuthService
{
    private const string TokenKey = "azuredevops_pat";

    private readonly HttpClient _http = new();

    public AzureDevOpsAuthState State     { get; private set; } = AzureDevOpsAuthState.NotFound;
    public string?              Username  { get; private set; }
    public string?              LastError { get; private set; }

    public event Action? StateChanged;

    public AzureDevOpsAuthService()
    {
        _ = InitializeStateAsync();
    }

    public Task<string?> GetTokenAsync() => GetStoredTokenAsync();

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
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($":{personalAccessToken}")));

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = (int)resp.StatusCode is 401 or 403
                    ? "Azure DevOps rejected that Personal Access Token."
                    : $"Azure DevOps sign-in failed: HTTP {(int)resp.StatusCode}";
                StateChanged?.Invoke();
                return false;
            }

            if (!await TryStoreTokenAsync(personalAccessToken))
            {
                LastError = "Unable to securely store the Azure DevOps token on this device.";
                StateChanged?.Invoke();
                return false;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Username = doc.RootElement.TryGetProperty("displayName", out var name) ? name.GetString() : null;

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

    public async Task<bool> RefreshAsync()
    {
        var stored = await GetStoredTokenAsync();
        State = string.IsNullOrEmpty(stored) ? AzureDevOpsAuthState.NotFound : AzureDevOpsAuthState.Authenticated;
        StateChanged?.Invoke();
        return State == AzureDevOpsAuthState.Authenticated;
    }

    public void SignOut()
    {
        try { SecureStorage.Default.Remove(TokenKey); } catch { /* best effort */ }

        Username  = null;
        State     = AzureDevOpsAuthState.NotFound;
        LastError = null;
        StateChanged?.Invoke();
    }

    private async Task InitializeStateAsync()
    {
        var stored = await GetStoredTokenAsync();
        if (!string.IsNullOrEmpty(stored))
        {
            State = AzureDevOpsAuthState.Authenticated;
            StateChanged?.Invoke();
        }
    }

    private static async Task<bool> TryStoreTokenAsync(string token)
    {
        try { await SecureStorage.Default.SetAsync(TokenKey, token); return true; }
        catch { return false; }
    }

    private static async Task<string?> GetStoredTokenAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(TokenKey);
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch { return null; }
    }
}
