using System.Diagnostics;
using DiffThis.AI.OpenAI.Services;
using DiffThis.AI.OpenAI.Tests.TestData;
using DiffThis.AI.Shared.Services;
using DiffThis.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace DiffThis.AI.OpenAI.Tests.Integration;

/// <summary>
/// Live integration tests against GitHub Models (GPT-4o).
///
/// Requires <c>gh auth login</c> to have been run on this machine.
/// All tests are skipped automatically if a token cannot be obtained.
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

    public async Task InitializeAsync()
    {
        _token = await GetGhTokenAsync();
        if (_token is not null)
            _svc = new CopilotService(new StaticTokenAuth(_token), new PromptService());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Small diff — should always succeed ───────────────────────────────

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

    // ── Medium diff — should succeed for GPT-4o ──────────────────────────

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

    // ── Boundary probe (~28k chars) ──────────────────────────────────────
    // Medium passes (~15k), large fails (~60k). This probes the midpoint.

    [Fact]
    public async Task Review_LargeMediumDiff_BoundaryProbe()
    {
        Skip_IfNoToken();
        await RunProbeAsync(DiffDataBuilder.LargeMediumDiff(), "LargeMedium");
    }

    /// <summary>
    /// Binary-search probes — progressively smaller diffs until we find the threshold.
    /// Each probe is a separate fact so results are visible individually.
    /// </summary>
    [Fact]
    public async Task Review_Probe_24k()
    {
        Skip_IfNoToken();
        // 5 files × 50 lines ≈ 24 000 chars
        await RunProbeAsync(DiffDataBuilder.BuildFilesPublic(5, 50), "24k");
    }

    [Fact]
    public async Task Review_Probe_18k()
    {
        Skip_IfNoToken();
        // 4 files × 40 lines ≈ 18 000 chars
        await RunProbeAsync(DiffDataBuilder.BuildFilesPublic(4, 40), "18k");
    }

    [Fact]
    public async Task Review_Probe_35k()
    {
        Skip_IfNoToken();
        // 5 files × 70 lines ≈ 35 000 chars
        await RunProbeAsync(DiffDataBuilder.BuildFilesPublic(5, 70), "35k");
    }

    [Fact]
    public async Task Review_Probe_30k()
    {
        Skip_IfNoToken();
        // 4 files × 70 lines ≈ 29 000 chars
        await RunProbeAsync(DiffDataBuilder.BuildFilesPublic(4, 70), "30k");
    }

    private async Task RunProbeAsync(DiffResult diff, string label)
    {
        var prompts = new PromptService();
        var (_, userContent) = prompts.BuildReviewPromptParts(diff);
        _out.WriteLine($"[Probe/{label}] diff user content: {userContent.Length} chars");

        var ex = await Record.ExceptionAsync(() => _svc!.ReviewDiffAsync(diff, Model));

        if (ex is null)
            _out.WriteLine($"[Probe/{label}] SUCCESS");
        else
            _out.WriteLine($"[Probe/{label}] FAILED — {ex.GetType().Name}: {ex.Message}");
    }

    // ── Large diff — baseline capture (records actual behaviour) ─────────
    // These tests do NOT assert pass/fail — they document the current
    // behaviour so we can compare before/after the message-split refactor.

    [Fact]
    public async Task Review_LargeDiff_BaselineCapture()
    {
        Skip_IfNoToken();
        var diff = DiffDataBuilder.LargeDiff();
        var prompts = new PromptService();
        var (_, userContent) = prompts.BuildReviewPromptParts(diff);
        _out.WriteLine($"[Baseline] Large diff user content: {userContent.Length} chars");

        var ex = await Record.ExceptionAsync(
            () => _svc!.ReviewDiffAsync(diff, Model));

        if (ex is null)
            _out.WriteLine("[Baseline/Review/Large] SUCCESS — model handled the full diff");
        else
            _out.WriteLine($"[Baseline/Review/Large] FAILED — {ex.GetType().Name}: {ex.Message}");

        // Intentionally not asserting — this is a baseline capture test.
        // After the message-split refactor, re-run and compare the output.
    }

    [Fact]
    public async Task Explain_LargeDiff_BaselineCapture()
    {
        Skip_IfNoToken();
        var diff = DiffDataBuilder.LargeDiff();

        var ex = await Record.ExceptionAsync(
            () => _svc!.ExplainDiffAsync(diff, Model));

        if (ex is null)
            _out.WriteLine("[Baseline/Explain/Large] SUCCESS — model handled the full diff");
        else
            _out.WriteLine($"[Baseline/Explain/Large] FAILED — {ex.GetType().Name}: {ex.Message}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Skip_IfNoToken()
    {
        if (_token is null)
            throw new SkipException("No GitHub token — run `gh auth login` and retry.");
    }

    private static async Task<string?> GetGhTokenAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var token = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    // Minimal ICopilotAuthService implementation that returns a pre-obtained token.
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
