using System.Diagnostics;

namespace DiffThis.AI.OpenAI.Services;

/// <summary>
/// Delegates authentication to the official Codex CLI. DiffThis never reads or
/// stores OpenAI credentials; the CLI owns ChatGPT OAuth and token refresh.
/// </summary>
public class OpenAiAuthService : IOpenAiAuthService
{
    private const string ExePrefKey = "openai_codex_exe_path";
    private readonly string? _codexExe;

    public OpenAiAuthState State      { get; private set; }
    public string?         StatusText { get; private set; }
    public string?         LastError  { get; private set; }

    public event Action? StateChanged;

    public OpenAiAuthService()
    {
        _codexExe = ResolveExe();
        State = _codexExe is null ? OpenAiAuthState.CliNotFound : OpenAiAuthState.NotAuthenticated;
        _ = RefreshAsync();
    }

    internal ProcessStartInfo CreateProcessStartInfo(bool visible = false)
    {
        if (_codexExe is null)
            throw new InvalidOperationException("Codex CLI is not installed. Install @openai/codex, then sign in with ChatGPT.");

        var ext = Path.GetExtension(_codexExe).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var wrapped = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = !visible,
            };
            wrapped.ArgumentList.Add("/c");
            wrapped.ArgumentList.Add(_codexExe);
            return wrapped;
        }

        return new ProcessStartInfo(_codexExe)
        {
            UseShellExecute = false,
            CreateNoWindow = !visible,
        };
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_codexExe is null)
        {
            SetState(OpenAiAuthState.CliNotFound, null, "Codex CLI was not found.");
            return;
        }

        try
        {
            var (exitCode, stdout, stderr) = await RunCaptureAsync(["login", "status"], ct);
            var status = string.Join(" ", new[] { stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (exitCode == 0 && !status.Contains("api key", StringComparison.OrdinalIgnoreCase))
            {
                SetState(OpenAiAuthState.Authenticated,
                    string.IsNullOrWhiteSpace(status) ? "Signed in with ChatGPT" : status, null);
                return;
            }

            var error = status.Contains("api key", StringComparison.OrdinalIgnoreCase)
                ? "DiffThis requires ChatGPT online sign-in, not API-key authentication. Sign out, then sign in with ChatGPT."
                : string.IsNullOrWhiteSpace(status) ? "Not signed in to OpenAI." : status;
            SetState(OpenAiAuthState.NotAuthenticated, null, error);
        }
        catch (Exception ex)
        {
            SetState(OpenAiAuthState.NotAuthenticated, null, ex.Message);
        }
    }

    public async Task<bool> TryInteractiveLoginAsync(CancellationToken ct = default)
    {
        if (_codexExe is null)
        {
            SetState(OpenAiAuthState.CliNotFound, null, "Codex CLI was not found.");
            return false;
        }

        Process? process = null;
        try
        {
            var psi = CreateProcessStartInfo(visible: true);
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.ArgumentList.Add("login");
            process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            SetState(OpenAiAuthState.NotAuthenticated, null, ex.Message);
            return false;
        }
        finally
        {
            process?.Dispose();
        }

        await RefreshAsync(ct);
        return State == OpenAiAuthState.Authenticated;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_codexExe is not null)
            await RunCaptureAsync(["logout"], ct);
        await RefreshAsync(ct);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        IEnumerable<string> args, CancellationToken ct)
    {
        var psi = CreateProcessStartInfo();
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Codex CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    private void SetState(OpenAiAuthState state, string? status, string? error)
    {
        State = state;
        StatusText = status;
        LastError = error;
        StateChanged?.Invoke();
    }

    private static string? ResolveExe()
    {
        var cached = Preferences.Get(ExePrefKey, "");
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached)) return cached;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(roaming, "npm", "codex.cmd"),
            Path.Combine(roaming, "npm", "codex.exe"),
            Path.Combine(home, ".local", "bin", "codex.exe"),
            Path.Combine(local, "Programs", "codex", "codex.exe"),
            @"C:\Program Files\codex\codex.exe",
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            Preferences.Set(ExePrefKey, candidate);
            return candidate;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in new[] { "codex.exe", "codex.cmd", "codex.bat" })
            {
                var fullPath = Path.Combine(directory, fileName);
                if (!File.Exists(fullPath)) continue;
                Preferences.Set(ExePrefKey, fullPath);
                return fullPath;
            }
        }

        return null;
    }
}
