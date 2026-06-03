using DiffThis.AI.OpenAI.Services;
using DiffThis.AI.OpenAI.Tests.TestData;
using DiffThis.AI.Shared.Services;
using NSubstitute;
using Xunit;

namespace DiffThis.AI.OpenAI.Tests.Unit;

/// <summary>
/// Tests that verify <see cref="CopilotService"/> behaviour when authentication
/// is unavailable.  Network-level 413/429 paths require the real Azure SDK client
/// and are covered by the integration tests.
/// </summary>
public class CopilotServiceErrorTests
{
    private readonly ICopilotAuthService _auth = Substitute.For<ICopilotAuthService>();
    private readonly PromptService       _prompts = new();

    [Fact]
    public async Task ReviewDiffAsync_ThrowsUnauthorized_WhenTokenIsNull()
    {
        _auth.GetSessionTokenAsync(default).ReturnsForAnyArgs(Task.FromResult<string?>(null));
        var svc = new CopilotService(_auth, _prompts);

        var ex = await Record.ExceptionAsync(
            () => svc.ReviewDiffAsync(DiffDataBuilder.SmallDiff(), "gpt-4o"));

        Assert.IsType<UnauthorizedAccessException>(ex);
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public async Task ExplainDiffAsync_ThrowsUnauthorized_WhenTokenIsNull()
    {
        _auth.GetSessionTokenAsync(default).ReturnsForAnyArgs(Task.FromResult<string?>(null));
        var svc = new CopilotService(_auth, _prompts);

        var ex = await Record.ExceptionAsync(
            () => svc.ExplainDiffAsync(DiffDataBuilder.SmallDiff(), "gpt-4o"));

        Assert.IsType<UnauthorizedAccessException>(ex);
    }
}
