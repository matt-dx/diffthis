using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiffThis.Core.Models;
using DiffThis.AI.Claude.Models;

namespace DiffThis.AI.Claude.Services;

public class ClaudeAuthService : IClaudeAuthService
{
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    // The public OAuth client ID registered for the Claude Code CLI application.
    private const string OAuthClientId   = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthTokenUrl   = "https://platform.claude.com/v1/oauth/token";

    private static readonly HttpClient _http = new();

    // Lazy so Preferences.Get is deferred until after MAUI's platform init
    private static readonly Lazy<string> _claudeExeLazy = new(ResolveExe);
    internal static string ClaudeExe => _claudeExeLazy.Value;

    private const string ClaudeExePrefKey = "claude_exe_path";

    private static string ResolveExe()
    {
        // Return the cached path from a previous session if it still exists
        var cached = Preferences.Get(ClaudeExePrefKey, "");
        if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
            return cached;

        // Search well-known locations (MAUI apps inherit a minimal PATH)
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, "AppData", "Local", "Programs", "claude", "claude.exe"),
            Path.Combine(home, "AppData", "Roaming", "npm", "claude.cmd"),
            @"C:\Program Files\claude\claude.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) { Preferences.Set(ClaudeExePrefKey, c); return c; }

        // Walk PATH entries explicitly as a last resort
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim(), "claude.exe");
            if (File.Exists(full)) { Preferences.Set(ClaudeExePrefKey, full); return full; }
        }

        return "claude"; // let the OS try; will fail with a clear message
    }

    // .cmd/.bat files cannot be launched directly with UseShellExecute=false;
    // they require cmd.exe /c as the host process.
    // Using ArgumentList (not the Arguments string) for all cases lets .NET handle
    // quoting via CreateProcess rules — no hand-rolled cmd.exe escaping needed.
    // visible=true is used for `claude auth login`, which needs a console the user can watch
    // (it prints an authorization URL and waits on the browser callback).
    internal static ProcessStartInfo CreateProcessStartInfo(bool visible = false)
    {
        var exe = ClaudeExe;
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow  = !visible,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
            return psi;
        }
        return new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow  = !visible,
        };
    }

    // Uniform arg append — ArgumentList works for both the direct and cmd-wrapped PSI.
    internal static void AppendArg(ProcessStartInfo psi, string arg)
        => psi.ArgumentList.Add(arg);

    private ClaudeCredentials? _creds;

    public ClaudeAuthState State            { get; private set; } = ClaudeAuthState.NotFound;
    public string?         AccessToken      => _creds?.AccessToken;
    public string?         SubscriptionType => _creds?.SubscriptionType;
    public string?         Email            { get; private set; }
    public bool            IsTokenExpired   => _creds?.IsExpired ?? true;

    public ClaudeAuthService() => Reload();

    public void Reload()
    {
        _creds = null;
        Email  = null;
        if (!File.Exists(CredentialsPath)) { State = ClaudeAuthState.NotFound; return; }

        try
        {
            using var stream = File.OpenRead(CredentialsPath);
            using var doc    = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            { State = ClaudeAuthState.NotFound; return; }

            var token = oauth.GetProperty("accessToken").GetString() ?? "";
            if (string.IsNullOrEmpty(token)) { State = ClaudeAuthState.NotFound; return; }

            _creds = new ClaudeCredentials
            {
                AccessToken      = token,
                RefreshToken     = oauth.GetProperty("refreshToken").GetString() ?? "",
                ExpiresAt        = oauth.GetProperty("expiresAt").GetInt64(),
                SubscriptionType = oauth.TryGetProperty("subscriptionType", out var st)
                                   ? st.GetString() ?? "" : "",
            };
            State = ClaudeAuthState.Authenticated;

            // Best-effort: read email from auth status asynchronously
            _ = TryReadEmailAsync();
        }
        catch { State = ClaudeAuthState.NotFound; }
    }

    /// <summary>
    /// Refreshes the OAuth access token via Anthropic's token endpoint using the stored
    /// refresh token, then reloads credentials from disk. Falls back to a disk-only reload
    /// if the refresh request fails (e.g. rate-limited or network error).
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        if (_creds is not null && !string.IsNullOrEmpty(_creds.RefreshToken))
            await TryOAuthRefreshAsync(_creds.RefreshToken);

        Reload();
        return State == ClaudeAuthState.Authenticated && !IsTokenExpired;
    }

    // POSTs to the Anthropic OAuth token endpoint with the refresh_token grant.
    // On success, writes updated access_token + expiresAt back to .credentials.json.
    private async Task TryOAuthRefreshAsync(string refreshToken)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new
                {
                    grant_type    = "refresh_token",
                    refresh_token = refreshToken,
                    client_id     = OAuthClientId,
                }),
                Encoding.UTF8,
                "application/json");

            var resp = await _http.PostAsync(OAuthTokenUrl, body, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;

            if (!root.TryGetProperty("access_token", out var atProp)) return;
            var newToken = atProp.GetString();
            if (string.IsNullOrEmpty(newToken)) return;

            // expires_in is in seconds; expiresAt is Unix ms
            var expiresIn = root.TryGetProperty("expires_in", out var expProp)
                ? expProp.GetInt64() : 28800L;
            var newExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;

            var newRefreshToken = root.TryGetProperty("refresh_token", out var rtProp)
                ? rtProp.GetString() : null;

            await PatchCredentialsFileAsync(newToken, newExpiresAt, newRefreshToken);
        }
        catch { /* network error or JSON parse failure — fall through to Reload */ }
    }

    // Reads the credentials file, patches the OAuth fields, and writes it back atomically.
    private static async Task PatchCredentialsFileAsync(
        string newAccessToken, long newExpiresAt, string? newRefreshToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(CredentialsPath);
            using var doc  = JsonDocument.Parse(json);
            using var ms   = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var topProp in doc.RootElement.EnumerateObject())
            {
                if (topProp.Name != "claudeAiOauth")
                { topProp.WriteTo(writer); continue; }

                writer.WritePropertyName("claudeAiOauth");
                writer.WriteStartObject();
                foreach (var p in topProp.Value.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "accessToken":
                            writer.WriteString("accessToken", newAccessToken);
                            break;
                        case "expiresAt":
                            writer.WriteNumber("expiresAt", newExpiresAt);
                            break;
                        case "refreshToken" when newRefreshToken is not null:
                            writer.WriteString("refreshToken", newRefreshToken);
                            break;
                        default:
                            p.WriteTo(writer);
                            break;
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            await writer.FlushAsync();

            // Write atomically via a temp file to avoid corrupting credentials on crash
            var tmp = CredentialsPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, ms.ToArray());
            File.Move(tmp, CredentialsPath, overwrite: true);
        }
        catch { /* best effort — leave existing credentials unchanged */ }
    }

    /// <summary>
    /// Launches `claude auth login` in a visible console window so the user can complete the
    /// browser-based OAuth flow, waits for it to exit, then reloads credentials from disk.
    /// </summary>
    public async Task<bool> TryInteractiveLoginAsync(CancellationToken ct = default)
    {
        Process? proc = null;
        try
        {
            var psi = CreateProcessStartInfo(visible: true);
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppendArg(psi, "auth");
            AppendArg(psi, "login");

            proc = Process.Start(psi);
            if (proc is null) return false;

            await proc.WaitForExitAsync(ct);
        }
        catch { return false; }
        finally { proc?.Dispose(); }

        Reload();
        return State == ClaudeAuthState.Authenticated;
    }

    private async Task TryReadEmailAsync()
    {
        using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Process?  proc = null;
        try
        {
            var psi = CreateProcessStartInfo();
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.WorkingDirectory       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppendArg(psi, "auth");
            AppendArg(psi, "status");

            proc = Process.Start(psi);
            if (proc is null) return;

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            if (proc.ExitCode != 0) return;

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("email", out var e))
                Email = e.GetString();
        }
        catch { /* best effort — timeout, process error, or JSON parse failure */ }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
            proc?.Dispose();
        }
    }
}
