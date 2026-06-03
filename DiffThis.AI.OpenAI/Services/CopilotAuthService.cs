using System.Diagnostics;
using System.Text.Json;

namespace DiffThis.Services;

public class CopilotAuthService : ICopilotAuthService
{
    private const string GhExePrefKey = "gh_exe_path";

    // GitHub OAuth tokens are long-lived; cache for 1 hour so we don't
    // spawn a subprocess on every model request.
    private string?  _cachedToken;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private string?  _ghExe;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HttpClient    _http = new();

    public CopilotAuthState State     { get; private set; } = CopilotAuthState.NotFound;
    public string?          Username  { get; private set; }
    public string?          LastError { get; private set; }

    public CopilotAuthService() => _ = RefreshAsync();

    // ── gh.exe discovery ──────────────────────────────────────────────────

    private string ResolveGhExe()
    {
        if (_ghExe is not null && File.Exists(_ghExe)) return _ghExe;

        var cached = Preferences.Get(GhExePrefKey, "");
        if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
            return _ghExe = cached;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "AppData", "Local", "Programs", "GitHub CLI", "gh.exe"),
            @"C:\Program Files\GitHub CLI\gh.exe",
            @"C:\Program Files (x86)\GitHub CLI\gh.exe",
            Path.Combine(home, "scoop", "shims", "gh.exe"),
            Path.Combine(home, "AppData", "Local", "Microsoft", "WinGet", "Links", "gh.exe"),
            @"C:\ProgramData\chocolatey\bin\gh.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) { Preferences.Set(GhExePrefKey, c); return _ghExe = c; }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "gh.exe", "gh.cmd" })
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) { Preferences.Set(GhExePrefKey, full); return _ghExe = full; }
            }
        }

        var found = WhereGh();
        if (found is not null) { Preferences.Set(GhExePrefKey, found); return _ghExe = found; }

        return _ghExe = "gh";
    }

    private static string? WhereGh()
    {
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/c where gh",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault()?.Trim();
            return first is not null && File.Exists(first) ? first : null;
        }
        catch { return null; }
        finally { p?.Dispose(); }
    }

    private static ProcessStartInfo MakeGhPsi(string exe)
    {
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
            return psi;
        }
        return new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// Returns the GitHub OAuth token from `gh auth token`.
    /// This is used directly as the Bearer token for GitHub Models API calls.
    public async Task<string?> GetSessionTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTime.UtcNow < _cacheExpiry)
                return _cachedToken;

            var exe = ResolveGhExe();
            var (token, error) = await RunGhAuthTokenAsync(exe, ct);

            if (token is null)
            {
                State      = CopilotAuthState.NotFound;
                LastError  = error;
                return null;
            }

            _cachedToken = token;
            _cacheExpiry = DateTime.UtcNow.AddHours(1);
            State        = CopilotAuthState.Authenticated;
            LastError    = null;

            if (Username is null)
                _ = TryReadUsernameAsync(token);

            return _cachedToken;
        }
        catch (Exception ex)
        {
            State     = CopilotAuthState.NotFound;
            LastError = ex.Message;
            return null;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        _cachedToken = null;
        _cacheExpiry = DateTime.MinValue;
        Username     = null;
        LastError    = null;
        _ghExe       = null;
        await GetSessionTokenAsync(ct);
        return State == CopilotAuthState.Authenticated;
    }

    private static async Task<(string? token, string? error)> RunGhAuthTokenAsync(
        string exe, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var psi = MakeGhPsi(exe);
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");

            proc = Process.Start(psi);
            if (proc is null) return (null, $"Failed to start process: {exe}");

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                var msg = string.IsNullOrWhiteSpace(stderr)
                    ? $"Exit code {proc.ExitCode}"
                    : stderr.Trim();
                return (null, $"`gh auth token` failed: {msg}. Is GitHub CLI installed?");
            }

            return (stdout.Trim(), null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not run `gh auth token`: {ex.Message}");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
            proc?.Dispose();
        }
    }

    private async Task TryReadUsernameAsync(string ghToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Add("Authorization", $"token {ghToken}");
            req.Headers.Add("User-Agent", "DiffThis/1.0");
            var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (doc.RootElement.TryGetProperty("login", out var login))
                Username = login.GetString();
        }
        catch { /* best effort */ }
    }
}
