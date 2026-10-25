using Kare.Abstractions;
using Kare.Service.Dashboard;
using Xunit;

namespace Kare.Tests;

public sealed class SavingsSummaryTests
{
    [Fact]
    public void AnthropicDescriptorRecordsTheAnthropicRoute()
    {
        var descriptor = new CloudModelDescriptor(
            "anthropic-complex",
            "claude-opus-5-5",
            CloudModelProvider.Anthropic,
            CloudModelTier.Complex,
            600,
            SupportsTools: true);

        Assert.Equal(KareRoute.Anthropic, descriptor.Route);
    }

    [Fact]
    public void SummarySeparatesSlmCacheAndCloudShares()
    {
        var usage = new[]
        {
            Usage(nameof(KareRoute.LocalSlm), successful: 6),
            Usage(nameof(KareRoute.Cache), successful: 2),
            Usage(nameof(KareRoute.Foundry), successful: 2),
        };
        var endpoints = new[]
        {
            Endpoint("local", estimatedCost: null, avoided: 0.05m),
            Endpoint("foundry-heavy", estimatedCost: 0.25m, avoided: null, pricedRequests: 2),
        };

        var summary = SavingsSummaryCalculator.Compute(usage, endpoints);

        Assert.Equal(10, summary.SuccessfulRequests);
        Assert.Equal(6, summary.SlmRequests);
        Assert.Equal(2, summary.CacheRequests);
        Assert.Equal(2, summary.CloudRequests);
        Assert.Equal(60.0, summary.SlmSharePercent);
        Assert.Equal(80.0, summary.OnDeviceSharePercent);
        Assert.Equal(0.25m, summary.EstimatedCloudCostUsd);
        Assert.Equal(2, summary.PricedCloudRequests);
        Assert.Equal(0.05m, summary.EstimatedAvoidedCostUsd);
    }

    [Fact]
    public void SummaryLeavesPercentsAndCostsUnknownWithoutData()
    {
        var summary = SavingsSummaryCalculator.Compute([], []);

        Assert.Equal(0, summary.SuccessfulRequests);
        Assert.Null(summary.SlmSharePercent);
        Assert.Null(summary.OnDeviceSharePercent);
        Assert.Null(summary.EstimatedCloudCostUsd);
        Assert.Null(summary.EstimatedAvoidedCostUsd);
    }

    private static DashboardMetrics.ModelUsage Usage(string route, long successful) => new(
        ModelId: "model",
        ProviderRouteId: null,
        Backend: "Local",
        Route: route,
        RequestCount: successful,
        SuccessfulRequests: successful,
        FailedRequests: 0,
        BillableRequests: 0,
        FallbackRequests: 0,
        InputTokens: 0,
        OutputTokens: 0,
        AverageTimeToFirstTokenMs: 0,
        AverageTotalDurationMs: 0,
        AverageDecodeTokensPerSecond: null,
        LastUsedAt: DateTime.UtcNow,
        PricedRequests: 0,
        PricedInputTokens: 0,
        PricedOutputTokens: 0,
        SuccessfulPricedRequests: 0,
        SuccessfulPricedInputTokens: 0,
        SuccessfulPricedOutputTokens: 0);

    private static DashboardMetrics.ModelEndpoint Endpoint(
        string id,
        decimal? estimatedCost,
        decimal? avoided,
        long pricedRequests = 0) => new(
        Id: id,
        Provider: "Test",
        ModelId: "model",
        WireModel: null,
        Endpoint: "test://endpoint",
        Tier: "Test",
        SupportsTools: false,
        RequestCount: 0,
        InputTokens: 0,
        OutputTokens: 0,
        LastUsedAt: null,
        AverageInputCostUsdPerMillionTokens: null,
        AverageOutputCostUsdPerMillionTokens: null,
        PricingSource: null,
        PricingAsOf: null,
        EstimatedCostUsd: estimatedCost,
        EstimatedCostRequests: pricedRequests,
        EstimatedAvoidedCostUsd: avoided);
}
