using System.Net;
using System.Text;
using System.Text.Json;
using DiffThis.AI.Shared.Services;
using DiffThis.Core.Models;

namespace DiffThis.AI.OpenAI.Services;

public class CopilotService : ICopilotService
{
    private static readonly Uri _endpoint =
        new("https://api.githubcopilot.com/chat/completions");

    private readonly ICopilotAuthService _auth;
    private readonly PromptService       _prompts;
    private readonly HttpClient          _http = new();

    public CopilotService(ICopilotAuthService auth, PromptService prompts)
    {
        _auth    = auth;
        _prompts = prompts;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default)
    {
        var (system, user) = _prompts.BuildReviewPromptParts(diff);
        return CallAsync(system, user, modelId, ct);
    }

    public Task<string> ExplainDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default)
    {
        var (system, user) = _prompts.BuildExplainPromptParts(diff);
        return CallAsync(system, user, modelId, ct);
    }

    private async Task<string> CallAsync(string system, string user, string modelId, CancellationToken ct)
    {
        var token = await _auth.GetSessionTokenAsync(ct)
            ?? throw new UnauthorizedAccessException(
                "GitHub Copilot is not signed in. Open Settings to sign in.");

        var body = JsonSerializer.Serialize(new
        {
            model    = modelId,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user   },
            },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Add("Authorization",         $"Bearer {token}");
        req.Headers.Add("Copilot-Integration-Id", "vscode-chat");
        req.Headers.Add("User-Agent",             "DiffThis/1.0");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GitHub Copilot request failed: {ex.Message}", ex);
        }

        var responseBody = await resp.Content.ReadAsStringAsync(ct);

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
                    throw new InvalidOperationException(
                        $"GitHub Copilot {(int)resp.StatusCode}: {responseBody}");
            }
        }

        // Parse OpenAI-format response
        try
        {
            using var doc  = JsonDocument.Parse(responseBody);
            var choices    = doc.RootElement.GetProperty("choices");
            var content    = choices[0].GetProperty("message").GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("GitHub Copilot returned an empty response.");

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
