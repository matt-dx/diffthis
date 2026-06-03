using DiffThis.AI.OpenAI.Tests.TestData;
using DiffThis.AI.Shared.Services;
using Xunit;

namespace DiffThis.AI.OpenAI.Tests.Unit;

public class PromptServiceTests
{
    private readonly PromptService _svc = new();

    // ── BuildReviewPrompt ─────────────────────────────────────────────────

    [Fact]
    public void BuildReviewPrompt_ContainsDiffHeaders()
    {
        var prompt = _svc.BuildReviewPrompt(DiffDataBuilder.SmallDiff());

        Assert.Contains("--- src/Repositories/UserRepository.cs", prompt);
        Assert.Contains("--- src/Services/AuthService.cs", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_ContainsAdditionsAndDeletions()
    {
        var prompt = _svc.BuildReviewPrompt(DiffDataBuilder.SmallDiff());

        Assert.Contains("+    public async Task<User?> GetByEmailAsync", prompt);
        Assert.Contains("-        throw new NotImplementedException();", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_ContainsRepoMetadata()
    {
        var prompt = _svc.BuildReviewPrompt(DiffDataBuilder.SmallDiff());

        Assert.Contains("sample-api", prompt);
        Assert.Contains("main", prompt);
        Assert.Contains("feature/email-lookup", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_NoUnresolvedPlaceholders()
    {
        var prompt = _svc.BuildReviewPrompt(DiffDataBuilder.MediumDiff());

        Assert.DoesNotContain("{{", prompt);
        Assert.DoesNotContain("}}", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_TruncatesAtMaxChars()
    {
        const int limit = 500;
        var prompt = _svc.BuildReviewPrompt(DiffDataBuilder.MediumDiff(), maxDiffChars: limit);

        Assert.Contains("truncated", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_DoesNotTruncateSmallDiff()
    {
        var prompt = _svc.BuildReviewPrompt(DiffDataBuilder.SmallDiff(), maxDiffChars: 60_000);

        Assert.DoesNotContain("truncated", prompt);
    }

    // ── BuildExplainPrompt ────────────────────────────────────────────────

    [Fact]
    public void BuildExplainPrompt_NoUnresolvedPlaceholders()
    {
        var prompt = _svc.BuildExplainPrompt(DiffDataBuilder.MediumDiff());

        Assert.DoesNotContain("{{", prompt);
        Assert.DoesNotContain("}}", prompt);
    }

    [Fact]
    public void BuildExplainPrompt_ContainsDiffContent()
    {
        var prompt = _svc.BuildExplainPrompt(DiffDataBuilder.SmallDiff());

        Assert.Contains("--- src/Repositories/UserRepository.cs", prompt);
    }

    // ── BuildReviewPromptParts ────────────────────────────────────────────
    // Tests for the new system/user split — added as part of the refactor.

    [Fact]
    public void BuildReviewPromptParts_SystemContainsInstructions()
    {
        var (system, _) = _svc.BuildReviewPromptParts(DiffDataBuilder.SmallDiff());

        // System part should have the instructions but NOT raw diff lines
        Assert.NotEmpty(system);
        Assert.DoesNotContain("+    public async Task", system);
    }

    [Fact]
    public void BuildReviewPromptParts_UserContainsDiffContent()
    {
        var (_, user) = _svc.BuildReviewPromptParts(DiffDataBuilder.SmallDiff());

        Assert.Contains("--- src/Repositories/UserRepository.cs", user);
        Assert.Contains("+    public async Task<User?> GetByEmailAsync", user);
    }

    [Fact]
    public void BuildReviewPromptParts_NeitherPartHasUnresolvedPlaceholders()
    {
        var (system, user) = _svc.BuildReviewPromptParts(DiffDataBuilder.MediumDiff());

        Assert.DoesNotContain("{{", system);
        Assert.DoesNotContain("{{", user);
    }

    [Fact]
    public void BuildExplainPromptParts_SystemContainsInstructions()
    {
        var (system, _) = _svc.BuildExplainPromptParts(DiffDataBuilder.SmallDiff());

        Assert.NotEmpty(system);
        Assert.DoesNotContain("+    public async Task", system);
    }

    [Fact]
    public void BuildExplainPromptParts_UserContainsDiffContent()
    {
        var (_, user) = _svc.BuildExplainPromptParts(DiffDataBuilder.SmallDiff());

        Assert.Contains("--- src/Repositories/UserRepository.cs", user);
    }

    // ── Char count sanity checks ──────────────────────────────────────────

    [Fact]
    public void SmallDiff_DiffContentUnder5000Chars()
    {
        var (_, user) = _svc.BuildReviewPromptParts(DiffDataBuilder.SmallDiff());

        Assert.True(user.Length < 5_000,
            $"Expected small diff to be under 5 000 chars, got {user.Length}");
    }
}
