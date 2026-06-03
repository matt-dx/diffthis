using DiffThis.AI.OpenAI.Services;
using DiffThis.AI.OpenAI.Tests.TestData;
using DiffThis.AI.Shared.Services;
using Xunit;
using Xunit.Abstractions;

namespace DiffThis.AI.OpenAI.Tests.Integration;

/// <summary>
/// Live integration tests against the GitHub Copilot API (GPT-4o).
///
/// Requires a valid Copilot session token set in the environment:
///   COPILOT_SESSION_TOKEN=&lt;token&gt;
///
/// A session token is obtained by signing in through the DiffThis Settings page
/// (GitHub Copilot → Sign in). All tests are skipped automatically when the
/// variable is absent.
///
/// Run selectively:
///   dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class CopilotIntegrationTests : IAsyncLifetime
{
    private const string Model = "gpt-4o";

    private readonly ITestOutputHelper _out;
    private string?                    _token;
    private CopilotService?            _svc;

    public CopilotIntegrationTests(ITestOutputHelper output) => _out = output;

    public Task InitializeAsync()
    {
        _token = Environment.GetEnvironmentVariable("COPILOT_SESSION_TOKEN");
        if (_token is not null)
            _svc = new CopilotService(new StaticTokenAuth(_token), new PromptService());
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Small diff ────────────────────────────────────────────────────────

    [Fact]
    public async Task Review_SmallDiff_Succeeds()
    {
        Skip_IfNoToken();
        var result = await _svc!.ReviewDiffAsync(DiffDataBuilder.SmallDiff(), Model);
        _out.WriteLine($"[Review/Small] {result.Length} chars returned");
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Explain_SmallDiff_Succeeds()
    {
        Skip_IfNoToken();
        var result = await _svc!.ExplainDiffAsync(DiffDataBuilder.SmallDiff(), Model);
        _out.WriteLine($"[Explain/Small] {result.Length} chars returned");
        Assert.NotEmpty(result);
    }

    // ── Medium diff ───────────────────────────────────────────────────────

    [Fact]
    public async Task Review_MediumDiff_Succeeds()
    {
        Skip_IfNoToken();
        var result = await _svc!.ReviewDiffAsync(DiffDataBuilder.MediumDiff(), Model);
        _out.WriteLine($"[Review/Medium] {result.Length} chars returned");
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Explain_MediumDiff_Succeeds()
    {
        Skip_IfNoToken();
        var result = await _svc!.ExplainDiffAsync(DiffDataBuilder.MediumDiff(), Model);
        _out.WriteLine($"[Explain/Medium] {result.Length} chars returned");
        Assert.NotEmpty(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Skip_IfNoToken()
    {
        if (_token is null)
            throw new SkipException(
                "COPILOT_SESSION_TOKEN not set. Sign in via DiffThis Settings and set the variable to run integration tests.");
    }

    // Minimal ICopilotAuthService that returns a pre-obtained session token.
    private sealed class StaticTokenAuth(string token) : ICopilotAuthService
    {
        public CopilotAuthState State     => CopilotAuthState.Authenticated;
        public string?          Username  => null;
        public string?          LastError => null;

        public event Action? StateChanged { add { } remove { } }

        public Task<string?> GetSessionTokenAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(token);

        public Task<bool> RefreshAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<DeviceFlowInfo?> StartDeviceFlowAsync(CancellationToken ct = default)
            => Task.FromResult<DeviceFlowInfo?>(null);

        public void SignOut() { }
    }
}

/// <summary>xUnit v2 skip helper — throws a special exception that xUnit interprets as a skip.</summary>
public sealed class SkipException(string reason) : Exception(reason);
