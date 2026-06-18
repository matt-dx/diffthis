using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DiffThis.AI.Shared.Services;
using DiffThis.Core.Models;
using DiffThis.Core.Services;

namespace DiffThis.AI.OpenAI.Services;

public class OllamaService : IOllamaService
{
    private readonly IOllamaEndpointService _endpoints;
    private readonly PromptService          _prompts;
    private readonly ILogService            _log;
    private readonly HttpClient             _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public OllamaService(IOllamaEndpointService endpoints, PromptService prompts, ILogService log)
    {
        _endpoints = endpoints;
        _prompts   = prompts;
        _log       = log;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string endpointId, string modelId, CancellationToken ct = default)
    {
        var (system, user) = _prompts.BuildReviewPromptParts(diff);
        return CallAsync(system, user, endpointId, modelId, "review", ct);
    }

    public Task<string> ExplainDiffAsync(DiffResult diff, string endpointId, string modelId, CancellationToken ct = default)
    {
        var (system, user) = _prompts.BuildExplainPromptParts(diff);
        return CallAsync(system, user, endpointId, modelId, "explain", ct);
    }

    private async Task<string> CallAsync(string system, string user, string endpointId, string modelId, string feature, CancellationToken ct)
    {
        var endpoint = _endpoints.GetEndpoint(endpointId)
            ?? throw new InvalidOperationException(
                $"Ollama endpoint '{endpointId}' not found. It may have been removed.");

        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        var chatUrl = $"{baseUrl}/api/chat";

        using var cts = endpoint.TimeoutSeconds is { } secs
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null) cts.CancelAfter(TimeSpan.FromSeconds(endpoint.TimeoutSeconds!.Value));
        var effectiveCt = cts?.Token ?? ct;

        // Estimate token count (~2.5 chars/token for code-heavy diffs), add 4096 for response headroom,
        // round up to nearest 1024, clamp to [4096, ceiling of PromptService.MaxDiffChars in tokens + buffer].
        // 3.5 chars/token underestimates code by ~25-30%; 2.5 is more conservative and avoids truncation.
        const int    ResponseBuffer   = 4096;
        const int    BlockSize        = 1024;
        const int    MinCtx           = 4096;
        const double CharsPerToken    = 2.5;
        // Max tokens needed = ceil(MaxDiffChars / CharsPerToken) + ResponseBuffer, rounded up to BlockSize.
        // Using integer arithmetic: ceil(60000 / 2.5) = 24000, + 4096 = 28096, round to 29696.
        const int MaxDiffTokens = (int)(PromptService.MaxDiffChars / CharsPerToken) + 1;
        const int MaxCtx        = ((MaxDiffTokens + ResponseBuffer + BlockSize - 1) / BlockSize) * BlockSize;
        var estimatedPromptTokens = (int)Math.Ceiling((system.Length + user.Length) / CharsPerToken);
        var rawCtx  = estimatedPromptTokens + ResponseBuffer;
        var numCtx  = Math.Clamp((int)Math.Ceiling((double)rawCtx / BlockSize) * BlockSize, MinCtx, MaxCtx);

        var body = JsonSerializer.Serialize(new
        {
            model    = modelId,
            stream   = false,
            options  = new { num_ctx = numCtx },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user   },
            },
        });

        _log.WriteRequest("ollama", modelId, feature,
            $"  endpoint: {chatUrl}  num_ctx: {numCtx}\n  system: {system.Length} chars  user: {user.Length} chars");

        using var req = new HttpRequestMessage(HttpMethod.Post, chatUrl);
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
            req.Headers.Add("Authorization", $"Bearer {endpoint.ApiKey}");
        req.Headers.Add("User-Agent", "DiffThis/1.0");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        var sw = Stopwatch.StartNew();
        try
        {
            resp = await _http.SendAsync(req, effectiveCt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (cts is not null && cts.IsCancellationRequested)
        {
            var msg = $"Ollama request timed out after {endpoint.TimeoutSeconds}s. Increase the timeout in Settings or set it to unlimited.";
            _log.WriteError("ollama", modelId, feature, msg);
            throw new InvalidOperationException(msg);
        }
        catch (Exception ex) when (ex is TaskCanceledException
                && baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            // localhost unreachable — retry transparently with 127.0.0.1
            var altUrl = chatUrl.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
            using var req2 = new HttpRequestMessage(HttpMethod.Post, altUrl);
            if (!string.IsNullOrEmpty(endpoint.ApiKey))
                req2.Headers.Add("Authorization", $"Bearer {endpoint.ApiKey}");
            req2.Headers.Add("User-Agent", "DiffThis/1.0");
            req2.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try { resp = await _http.SendAsync(req2, effectiveCt); }
            catch (Exception ex2)
            {
                _log.WriteError("ollama", modelId, feature, ex2.Message);
                throw new InvalidOperationException(
                    $"Could not reach Ollama at {baseUrl} (also tried 127.0.0.1): {ex2.Message}", ex2);
            }
        }
        catch (Exception ex)
        {
            _log.WriteError("ollama", modelId, feature, ex.Message);
            throw new InvalidOperationException(
                $"Could not reach Ollama at {baseUrl}: {ex.Message}", ex);
        }

        var responseBody = await resp.Content.ReadAsStringAsync(effectiveCt);
        sw.Stop();
        _log.WriteResponse("ollama", modelId, feature, responseBody, sw.ElapsedMilliseconds);

        if (!resp.IsSuccessStatusCode)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new InvalidOperationException(
                        $"Ollama endpoint not found at {chatUrl}. Check the base URL in Settings.");
                case HttpStatusCode.Unauthorized:
                    throw new UnauthorizedAccessException(
                        "Ollama rejected the request — check the API key in Settings.");
                default:
                    if (responseBody.Contains("model") && responseBody.Contains("not found"))
                        throw new InvalidOperationException(
                            $"Model \"{modelId}\" not found in Ollama. Run 'ollama pull {modelId}' to download it.");
                    throw new InvalidOperationException(
                        $"Ollama {(int)resp.StatusCode}: {responseBody}");
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var content   = doc.RootElement
                               .GetProperty("message")
                               .GetProperty("content")
                               .GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Ollama returned an empty response.");

            return content.Trim();
        }
        catch (Exception ex) when (ex is not InvalidOperationException
                                       and not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not parse Ollama response: {ex.Message}\n{responseBody}", ex);
        }
    }
}
