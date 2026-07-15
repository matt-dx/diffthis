using System.Diagnostics;
using DiffThis.AI.Shared.Services;
using DiffThis.Core.Models;
using DiffThis.Core.Services;

namespace DiffThis.AI.OpenAI.Services;

public class OpenAiService : IOpenAiService
{
    private readonly IOpenAiAuthService _auth;
    private readonly OpenAiAuthService  _processFactory;
    private readonly PromptService      _prompts;
    private readonly ILogService        _log;

    public OpenAiService(IOpenAiAuthService auth, PromptService prompts, ILogService log)
    {
        _auth = auth;
        _processFactory = auth as OpenAiAuthService
            ?? throw new InvalidOperationException("OpenAI authentication must use the Codex CLI service.");
        _prompts = prompts;
        _log = log;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string model, CancellationToken ct = default)
        => CallAsync(_prompts.BuildReviewPrompt(diff), model, "review", ct);

    public Task<string> ExplainDiffAsync(DiffResult diff, string model, CancellationToken ct = default)
        => CallAsync(_prompts.BuildExplainPrompt(diff), model, "explain", ct);

    private async Task<string> CallAsync(string prompt, string model, string feature, CancellationToken ct)
    {
        await _auth.RefreshAsync(ct);
        if (_auth.State != OpenAiAuthState.Authenticated)
            throw new UnauthorizedAccessException(_auth.LastError ?? "Sign in to OpenAI with ChatGPT in Settings.");

        var psi = _processFactory.CreateProcessStartInfo();
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Non-interactive, read-only and ephemeral: this provider is only being used
        // as a model transport and cannot modify the user's repository.
        foreach (var arg in new[]
                 {
                     // These are global Codex options. Older CLI releases reject
                     // --ask-for-approval when it appears after the exec subcommand.
                     "--sandbox", "read-only", "--ask-for-approval", "never", "exec",
                     "--skip-git-repo-check", "--ephemeral", "--color", "never",
                 })
            psi.ArgumentList.Add(arg);
        if (!string.Equals(model, "default", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }
        psi.ArgumentList.Add("-");

        _log.WriteRequest("openai", model, feature, $"  auth: ChatGPT OAuth  prompt: {prompt.Length} chars");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Codex CLI.");
        // Write bytes directly so Windows console/code-page settings cannot
        // transcode smart punctuation or non-ASCII diff content before Codex reads it.
        var promptBytes = System.Text.Encoding.UTF8.GetBytes(prompt);
        await process.StandardInput.BaseStream.WriteAsync(promptBytes, ct);
        await process.StandardInput.BaseStream.FlushAsync(ct);
        process.StandardInput.Close();

        var sw = Stopwatch.StartNew();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        sw.Stop();
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        _log.WriteResponse("openai", model, feature,
            $"  exit: {process.ExitCode}\n  stdout: {stdout.Length} chars\n{(stderr.Length > 0 ? $"  stderr: {stderr}\n" : "")}",
            sw.ElapsedMilliseconds);

        if (process.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (error.Contains("login", StringComparison.OrdinalIgnoreCase)
                || error.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || error.Contains("401", StringComparison.OrdinalIgnoreCase))
            {
                await _auth.RefreshAsync(ct);
                throw new UnauthorizedAccessException("OpenAI session is unavailable. Sign in with ChatGPT again in Settings.");
            }
            if (error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || error.Contains("429"))
                throw new HttpRequestException("OpenAI rate limit reached. Wait a moment and try again.");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Codex returned no output." : error);
        }

        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("Codex returned an empty response.");
        return stdout;
    }
}
