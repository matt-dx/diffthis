using System.Diagnostics;
using System.Text.Json;
using DiffThis.Models;

namespace DiffThis.Services;

public class ClaudeAuthService : IClaudeAuthService
{
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

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
    // they require cmd.exe /c as the host process. Use this helper everywhere
    // instead of constructing ProcessStartInfo(ClaudeExe) directly.
    internal static ProcessStartInfo CreateProcessStartInfo()
    {
        var exe = ClaudeExe;
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            // cmd.exe /c cannot use ArgumentList alongside Arguments, so callers
            // must append extra args via AppendArg() rather than ArgumentList.Add().
            return new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
                Arguments       = $"/c \"{exe}\"",
            };
        }
        return new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
    }

    // Appends a single argument, handling both the cmd-wrapped and direct cases.
    // When cmd-wrapped, every arg is quoted using the CommandLineToArgvW algorithm
    // so that cmd.exe metacharacters (&, |, <, >, ^) inside the quoted string are
    // treated as literals and cannot be used for command injection.
    internal static void AppendArg(ProcessStartInfo psi, string arg)
    {
        if (psi.FileName == "cmd.exe")
            psi.Arguments += " " + QuoteArgForCmd(arg);
        else
            psi.ArgumentList.Add(arg);
    }

    // CommandLineToArgvW-compatible quoting: always wraps in " and correctly
    // handles backslashes adjacent to quote characters.
    private static string QuoteArgForCmd(string arg)
    {
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
                backslashes = 0;
            }
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
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
            // Note: credentials may have an expired access token. That's fine — we invoke
            // the claude CLI as a subprocess for all AI calls, which handles OAuth refresh
            // internally. We only need to know the credentials file exists.
            State = ClaudeAuthState.Authenticated;

            // Best-effort: read email from auth status asynchronously
            _ = TryReadEmailAsync();
        }
        catch { State = ClaudeAuthState.NotFound; }
    }

    // Note: RefreshAsync simply reloads from disk. Actual OAuth token refresh is handled
    // transparently by the claude CLI subprocess. DiffThis does call the Anthropic API
    // directly for model discovery (see ClaudeModelService), but not for completion requests.
    public Task<bool> RefreshAsync()
    {
        Reload();
        return Task.FromResult(State == ClaudeAuthState.Authenticated);
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
            AppendArg(psi, "--output-format");
            AppendArg(psi, "json");

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
            // Kill the process if it's still running after timeout
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
            proc?.Dispose();
        }
    }
}
