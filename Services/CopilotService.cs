using Azure;
using Azure.AI.Inference;
using DiffThis.Models;

namespace DiffThis.Services;

public class CopilotService : ICopilotService
{
    private readonly ICopilotAuthService _auth;
    private readonly PromptService       _prompts;

    private static readonly Uri _endpoint =
        new("https://models.inference.ai.azure.com");

    private ChatCompletionsClient? _client;
    private string?                _clientToken;

    public CopilotService(ICopilotAuthService auth, PromptService prompts)
    {
        _auth    = auth;
        _prompts = prompts;
    }

    public Task<string> ReviewDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default)
        => CallAsync(_prompts.BuildReviewPrompt(diff), modelId, ct);

    public Task<string> ExplainDiffAsync(DiffResult diff, string modelId, CancellationToken ct = default)
        => CallAsync(_prompts.BuildExplainPrompt(diff), modelId, ct);

    private async Task<string> CallAsync(string prompt, string modelId, CancellationToken ct)
    {
        var token = await _auth.GetSessionTokenAsync(ct)
            ?? throw new UnauthorizedAccessException(
                "GitHub Models is not authenticated. Run `gh auth login` in a terminal.");

        if (_client is null || _clientToken != token)
        {
            _client      = new ChatCompletionsClient(_endpoint, new AzureKeyCredential(token));
            _clientToken = token;
        }

        var client = _client;

        ChatCompletionsOptions options = new()
        {
            Model    = modelId,
            Messages = { new ChatRequestUserMessage(prompt) },
        };

        Response<ChatCompletions> response;
        try
        {
            response = await client.CompleteAsync(options, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 401)
        {
            throw new UnauthorizedAccessException(
                "GitHub Models: token rejected. Run `gh auth login` in a terminal.", ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            throw new HttpRequestException(
                "GitHub Models rate limit reached. Wait a moment and try again.", ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 413 || ex.Message.Contains("tokens_limit_reached"))
        {
            throw new InvalidOperationException(
                $"Diff is too large for \"{modelId}\". Use a model with a larger context window.", ex);
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"GitHub Models {ex.Status}: {ex.Message}", ex);
        }

        var text = response.Value.Content?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("GitHub Models returned an empty response.");

        return text;
    }
}
