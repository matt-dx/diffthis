using System.Diagnostics;
using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

public class ClaudeAuthService : IClaudeAuthService
{
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    internal static readonly string ClaudeExe = ResolveExe();

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

    private ClaudeCredentials? _creds;

    public ClaudeAuthState State            { get; private set; } = ClaudeAuthState.NotFound;
    public string?         AccessToken      => _creds?.AccessToken;
    public string?         SubscriptionType => _creds?.SubscriptionType;
    public string?         Email            { get; private set; }

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

            // Best-effort: read email from auth status cache if available
            _ = TryReadEmailAsync();
        }
        catch { State = ClaudeAuthState.NotFound; }
    }

    public Task<bool> RefreshAsync()
    {
        Reload();
        return Task.FromResult(State == ClaudeAuthState.Authenticated);
    }

    private async Task TryReadEmailAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(ClaudeExe)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WorkingDirectory       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");

            using var proc = Process.Start(psi);
            if (proc is null) return;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return;

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("email", out var e))
                Email = e.GetString();
        }
        catch { /* best effort */ }
    }
}
