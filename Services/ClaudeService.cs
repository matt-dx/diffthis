using System.Diagnostics;
using System.Text;
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

    public ClaudeService(IClaudeAuthService auth, IClaudeModelService models)
    {
        _auth   = auth;
        _models = models;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default)
        => CallAsync(BuildReviewPrompt(diff), model, toolsEnabled, maxTurns, ct);

    public Task<string> ExplainDiffAsync(DiffResult diff, string model, bool toolsEnabled, int maxTurns, CancellationToken ct = default)
        => CallAsync(BuildExplainPrompt(diff), model, toolsEnabled, maxTurns, ct);

    private async Task<string> CallAsync(string prompt, string model, bool toolsEnabled, int maxTurns, CancellationToken ct)
    {
        if (_auth.State != ClaudeAuthState.Authenticated)
            throw new InvalidOperationException("Not connected to Claude. Check Settings.");

        var psi = new ProcessStartInfo(ClaudeAuthService.ClaudeExe)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
            CreateNoWindow         = true,
            WorkingDirectory       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model");         psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("text");
        if (!toolsEnabled)
        {
            psi.ArgumentList.Add("--allowedTools"); psi.ArgumentList.Add("none");
        }
        if (maxTurns > 0)
        {
            psi.ArgumentList.Add("--max-turns"); psi.ArgumentList.Add(maxTurns.ToString());
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

    // ── Prompt builders ───────────────────────────────────────────────────

    private static string BuildReviewPrompt(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Review the following code diff from \"{diff.RepositoryName}\".");
        sb.AppendLine($"Comparing: {diff.BaseDisplay} → {diff.CompareDisplay}");
        sb.AppendLine($"{diff.Files.Count} files changed, +{diff.TotalAdditions} -{diff.TotalDeletions} lines");
        sb.AppendLine();
        sb.AppendLine("Identify bugs, logic errors, security issues, and notable improvements. " +
                      "Reference specific file names and line numbers. Be concise and direct.");
        sb.AppendLine();
        AppendDiffContent(sb, diff);
        return sb.ToString();
    }

    private static string BuildExplainPrompt(DiffResult diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Explain the following code changes from \"{diff.RepositoryName}\" in plain English.");
        sb.AppendLine($"Comparing: {diff.BaseDisplay} → {diff.CompareDisplay}");
        sb.AppendLine($"{diff.Files.Count} files changed, +{diff.TotalAdditions} -{diff.TotalDeletions} lines");
        sb.AppendLine();
        sb.AppendLine("Describe what was changed, the likely intent, and the impact. " +
                      "Write for a developer who needs a quick orientation to these changes.");
        sb.AppendLine();
        AppendDiffContent(sb, diff);
        return sb.ToString();
    }

    private static void AppendDiffContent(StringBuilder sb, DiffResult diff)
    {
        const int maxChars = 60_000;
        var written  = 0;
        var truncated = false;

        foreach (var file in diff.Files)
        {
            if (truncated) break;
            if (written >= maxChars) { sb.AppendLine("\n... (diff truncated due to length)"); break; }
            sb.AppendLine($"--- {file.DisplayPath}");

            if (file.IsBinary) { sb.AppendLine("[binary file]"); continue; }

            foreach (var hunk in file.Hunks)
            {
                if (truncated) break;
                var ctx = hunk.Context.Length > 0 ? " " + hunk.Context : "";
                sb.AppendLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@{ctx}");

                foreach (var line in hunk.Lines)
                {
                    var sign = line.Type switch
                    {
                        DiffLineType.Addition => "+",
                        DiffLineType.Deletion => "-",
                        _                     => " ",
                    };
                    sb.AppendLine($"{sign}{line.Content}");
                    written += line.Content.Length + 3; // sign(1) + content + \r\n(2)
                    if (written < maxChars) continue;
                    sb.AppendLine("\n... (diff truncated due to length)");
                    truncated = true;
                    break;
                }
            }
        }
    }
}
