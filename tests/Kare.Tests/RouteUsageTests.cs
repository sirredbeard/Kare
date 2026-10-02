using Kare.Abstractions;
using Xunit;

namespace Kare.Tests;

public sealed class RouteUsageTests
{
    [Fact]
    public void DecodeRateExcludesPromptProcessing()
    {
        // 101 output tokens, 1 second of prompt processing, 3 seconds total.
        // Decode covers the 100 tokens after the first, over the 2 remaining seconds.
        var usage = new RouteUsage(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            InputTokens: 2048,
            OutputTokens: 101,
            Succeeded: true);

        Assert.Equal(50.0, usage.DecodeTokensPerSecond!.Value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(1L)]
    public void DecodeRateIsUnknownWithoutAtLeastTwoOutputTokens(long? outputTokens)
    {
        var usage = new RouteUsage(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            InputTokens: 10,
            OutputTokens: outputTokens,
            Succeeded: true);

        Assert.Null(usage.DecodeTokensPerSecond);
    }

    [Fact]
    public void DecodeRateIsUnknownWhenTotalDoesNotExceedFirstToken()
    {
        var usage = new RouteUsage(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            InputTokens: 10,
            OutputTokens: 50,
            Succeeded: true);

        Assert.Null(usage.DecodeTokensPerSecond);
    }

    [Fact]
    public void FallbackIsOnlyReportedWhenARouteWasActuallyReplaced()
    {
        var direct = new RouteDecision(
            KareRoute.LocalSlm, "policy", "phi-4-mini", BackendKind.OnnxGenAiCpu, IsBillable: false);

        var demoted = direct with { FellBackFrom = KareRoute.CopilotHeavy };

        Assert.False(direct.IsFallback);
        Assert.True(demoted.IsFallback);
    }
}
