using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DiffThis.AI.Shared.Services;
using DiffThis.Core.Models;
using DiffThis.Core.Services;

namespace DiffThis.AI.OpenAI.Services;

public class CopilotService : ICopilotService
{
    private static readonly Uri _endpoint =
        new("https://api.githubcopilot.com/chat/completions");

    private readonly ICopilotAuthService  _auth;
    private readonly ICopilotModelService _models;
    private readonly PromptService        _prompts;
    private readonly ISettingsService     _settings;
    private readonly ILogService          _log;
    private readonly HttpClient           _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public CopilotService(ICopilotAuthService auth, ICopilotModelService models, PromptService prompts, ISettingsService settings, ILogService log)
    {
        _auth     = auth;
        _models   = models;
        _prompts  = prompts;
        _settings = settings;
        _log      = log;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default)
    {
        var (system, user) = _prompts.BuildReviewPromptParts(diff);
        return CallAsync(system, user, modelId, "review", ct);
    }

    public Task<string> ExplainDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default)
    {
        var (system, user) = _prompts.BuildExplainPromptParts(diff);
        return CallAsync(system, user, modelId, "explain", ct);
    }

    private async Task<string> CallAsync(string system, string user, string modelId, string feature, CancellationToken ct)
    {
        var token = await _auth.GetSessionTokenAsync(ct)
            ?? throw new UnauthorizedAccessException(
                "GitHub Copilot is not signed in. Open Settings to sign in.");

        // Thinking models (e.g. Claude Opus 4.x) must have reasoning_effort set via Copilot's
        // OpenAI-compatible endpoint. Without it the model defaults to high effort and
        // consumes all output tokens on reasoning, returning choices:[].
        // The Anthropic-native thinking.budget_tokens parameter is silently dropped here.
        var reasoningEffort = _models.GetReasoningEffort(modelId);
        string body;
        if (reasoningEffort is not null)
        {
            body = JsonSerializer.Serialize(new
            {
                model            = modelId,
                max_tokens       = 16_000,
                reasoning_effort = reasoningEffort,
                messages         = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user",   content = user   },
                },
            });
        }
        else
        {
            body = JsonSerializer.Serialize(new
            {
                model      = modelId,
                max_tokens = 4096,
                messages   = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user",   content = user   },
                },
            });
        }

        _log.WriteRequest("copilot", modelId, feature,
            $"  system: {system.Length} chars  user: {user.Length} chars\n  {body[..Math.Min(body.Length, 300)]}…");

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Add("Authorization",         $"Bearer {token}");
        req.Headers.Add("Copilot-Integration-Id", "vscode-chat");
        req.Headers.Add("User-Agent",             "DiffThis/1.0");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var cts = _settings.CopilotTimeoutSeconds is { } secs
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        cts?.CancelAfter(TimeSpan.FromSeconds(_settings.CopilotTimeoutSeconds!.Value));
        var token2 = cts?.Token ?? ct;

        HttpResponseMessage resp;
        var sw = Stopwatch.StartNew();
        try
        {
            resp = await _http.SendAsync(req, token2);
        }
        catch (OperationCanceledException) when (cts is not null && cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var msg = $"GitHub Copilot request timed out after {_settings.CopilotTimeoutSeconds}s. Increase the timeout in Settings or set it to unlimited.";
            _log.WriteError("copilot", modelId, feature, msg);
            throw new InvalidOperationException(msg);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.WriteError("copilot", modelId, feature, ex.Message);
            throw new InvalidOperationException($"GitHub Copilot request failed: {ex.Message}", ex);
        }

        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();
        _log.WriteResponse("copilot", modelId, feature, responseBody, sw.ElapsedMilliseconds);

        if (!resp.IsSuccessStatusCode)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new UnauthorizedAccessException(
                        "GitHub Copilot session token rejected. Open Settings to sign in again.");

                case HttpStatusCode.TooManyRequests:
                    throw new HttpRequestException(
                        "GitHub Copilot rate limit reached. Wait a moment and try again.");

                case HttpStatusCode.RequestEntityTooLarge:
                    throw new InvalidOperationException(
                        $"Diff is too large for \"{modelId}\".");

                default:
                    if (responseBody.Contains("tokens_limit_reached"))
                        throw new InvalidOperationException(
                            $"Diff is too large for \"{modelId}\".");
                    if (responseBody.Contains("unsupported_api_for_model"))
                        throw new InvalidOperationException(
                            $"\"{modelId}\" does not support chat completions and cannot be used for diff analysis.");
                    throw new InvalidOperationException(
                        $"GitHub Copilot {(int)resp.StatusCode}: {responseBody}");
            }
        }

        // Parse OpenAI-format response
        try
        {
            using var doc  = JsonDocument.Parse(responseBody);
            var choices    = doc.RootElement.GetProperty("choices");

            if (choices.GetArrayLength() == 0)
            {
                var hint = reasoningEffort is not null
                    ? $" For thinking models, try raising the reasoning effort in Settings > Copilot (current: \"{reasoningEffort}\")."
                    : " Try a smaller diff or a model with a higher output limit.";
                throw new InvalidOperationException(
                    $"\"{modelId}\" returned no content — the model used all output tokens before producing a response.{hint}");
            }

            var choice     = choices[0];
            var content    = choice.GetProperty("message").GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                // finish_reason "length" means the output was cut mid-stream
                var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
                if (finishReason == "length")
                    throw new InvalidOperationException(
                        $"\"{modelId}\" hit its maximum output token limit and the response was truncated. " +
                        "Try a smaller diff or a model with a higher output limit.");

                throw new InvalidOperationException("GitHub Copilot returned an empty response.");
            }

            return content.Trim();
        }
        catch (Exception ex) when (ex is not InvalidOperationException
                                       and not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not parse GitHub Copilot response: {ex.Message}\n{responseBody}", ex);
        }
    }
}
