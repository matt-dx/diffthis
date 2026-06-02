using System.Diagnostics;
using DiffThis.Models;

namespace DiffThis.Services;

/// Thrown when the claude CLI rejects a model ID as unknown or unavailable.
public class ModelUnavailableException(string modelId)
    : Exception($"Model \"{modelId}\" is not currently available.")
{
    public string ModelId { get; } = modelId;
}

public class ClaudeService : IClaudeService
{
    private readonly IClaudeAuthService  _auth;
    private readonly IClaudeModelService _models;
    private readonly PromptService       _prompts;

    public ClaudeService(IClaudeAuthService auth, IClaudeModelService models, PromptService prompts)
    {
        _auth    = auth;
        _models  = models;
        _prompts = prompts;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default)
        => CallAsync(_prompts.BuildReviewPrompt(diff), model, toolsEnabled, maxTurns, ct);

    public Task<string> ExplainDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default)
        => CallAsync(_prompts.BuildExplainPrompt(diff), model, toolsEnabled, maxTurns, ct);

    private async Task<string> CallAsync(string prompt, string model, bool toolsEnabled, int maxTurns, CancellationToken ct)
    {
        if (_auth.State != ClaudeAuthState.Authenticated)
            throw new InvalidOperationException("Not connected to Claude. Check Settings.");

        var psi = ClaudeAuthService.CreateProcessStartInfo();
        psi.RedirectStandardInput  = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding  = System.Text.Encoding.UTF8;
        psi.WorkingDirectory       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ClaudeAuthService.AppendArg(psi, "-p");
        ClaudeAuthService.AppendArg(psi, "--model");         ClaudeAuthService.AppendArg(psi, model);
        ClaudeAuthService.AppendArg(psi, "--output-format"); ClaudeAuthService.AppendArg(psi, "text");
        if (!toolsEnabled)
        {
            ClaudeAuthService.AppendArg(psi, "--allowedTools"); ClaudeAuthService.AppendArg(psi, "none");
        }
        if (maxTurns > 0)
        {
            ClaudeAuthService.AppendArg(psi, "--max-turns"); ClaudeAuthService.AppendArg(psi, maxTurns.ToString());
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process.");

        await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (proc.ExitCode != 0)
        {
            var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

            // Unknown/removed model — trigger a background refresh and let the caller handle it
            if (IsModelUnavailableError(err))
            {
                _ = _models.RefreshAsync();
                throw new ModelUnavailableException(model);
            }

            if (err.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || err.Contains("429"))
                throw new HttpRequestException("Rate limit reached. Wait a moment and try again.");
            if (err.Contains("auth") || err.Contains("login") || err.Contains("401"))
                throw new UnauthorizedAccessException(
                    "Claude session expired. Run `claude auth login` in your terminal, then reload in Settings.");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "claude returned no output." : err);
        }

        return stdout;
    }

    private static bool IsModelUnavailableError(string err) =>
        err.Contains("unknown model",     StringComparison.OrdinalIgnoreCase) ||
        err.Contains("invalid model",     StringComparison.OrdinalIgnoreCase) ||
        err.Contains("does not exist",    StringComparison.OrdinalIgnoreCase) ||
        err.Contains("no such model",     StringComparison.OrdinalIgnoreCase) ||
        err.Contains("model not found",   StringComparison.OrdinalIgnoreCase);

}
