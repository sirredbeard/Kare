using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Kare.Cloud.Copilot;
using Kare.Core.Inference;
using Kare.Inference.GenieX;
using Kare.Service;
using Kare.Service.Dashboard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class ModelEndpointPricingTests
{
    [Fact]
    public void CloudCostUsesOnlyRequestsWithBothTokenCounts()
    {
        var collector = new InMemoryMetricsCollector();
        collector.RecordRequest(CreateRequest(
            modelId: "cloud-model",
            route: "CopilotLight",
            providerRouteId: "cloud-fast",
            inputTokens: 1_000_000,
            outputTokens: 100_000,
            isBillable: true,
            succeeded: true));
        collector.RecordRequest(CreateRequest(
            modelId: "cloud-model",
            route: "CopilotLight",
            providerRouteId: "cloud-fast",
            inputTokens: null,
            outputTokens: 20,
            isBillable: true,
            succeeded: false));

        var endpoint = CreateProvider(
            new CloudModelRouteOptions
            {
                Id = "cloud-fast",
                ModelId = "cloud-model",
                AverageInputCostUsdPerMillionTokens = 2m,
                AverageOutputCostUsdPerMillionTokens = 10m,
                PricingSource = "https://pricing.example/models",
                PricingAsOf = new DateOnly(2026, 10, 1),
            }).GetEndpoints(collector.GetModelUsage()).Single(item => item.Id == "cloud-fast");

        Assert.Equal(3.0m, endpoint.EstimatedCostUsd);
        Assert.Equal(1, endpoint.EstimatedCostRequests);
        Assert.Equal(new DateOnly(2026, 10, 1), endpoint.PricingAsOf);
    }

    [Fact]
    public void LocalSavingsUseAverageConfiguredCloudPricesAndSuccessfulMeasuredTokens()
    {
        var collector = new InMemoryMetricsCollector();
        collector.RecordRequest(CreateRequest(
            modelId: "local-model",
            route: "LocalSlm",
            providerRouteId: null,
            inputTokens: 1_000_000,
            outputTokens: 500_000,
            isBillable: false,
            succeeded: true));
        collector.RecordRequest(CreateRequest(
            modelId: "local-model",
            route: "LocalSlm",
            providerRouteId: null,
            inputTokens: 2_000_000,
            outputTokens: 1_000_000,
            isBillable: false,
            succeeded: false));

        var provider = CreateProvider(
            new CloudModelRouteOptions
            {
                Id = "cloud-one",
                ModelId = "cloud-model-one",
                AverageInputCostUsdPerMillionTokens = 2m,
                AverageOutputCostUsdPerMillionTokens = 4m,
                PricingSource = "https://pricing.example/one",
                PricingAsOf = new DateOnly(2026, 10, 1),
            },
            new CloudModelRouteOptions
            {
                Id = "cloud-two",
                ModelId = "cloud-model-two",
                AverageInputCostUsdPerMillionTokens = 4m,
                AverageOutputCostUsdPerMillionTokens = 8m,
                PricingSource = "https://pricing.example/two",
                PricingAsOf = new DateOnly(2026, 10, 1),
            });
        var endpoint = provider.GetEndpoints(collector.GetModelUsage()).Single(item => item.Id == "local");

        Assert.Equal(6m, endpoint.EstimatedAvoidedCostUsd);
        Assert.Equal(6m, endpoint.AverageEstimatedAvoidedCostPerRequestUsd);
        Assert.Equal(1, endpoint.EstimatedAvoidedCostRequests);
        Assert.Equal(2, endpoint.AveragePriceModelCount);
        Assert.Null(endpoint.EstimatedCostUsd);
    }

    [Fact]
    public void CostEstimatesStayUnknownWithoutPricesOrCompleteTokenUsage()
    {
        var collector = new InMemoryMetricsCollector();
        collector.RecordRequest(CreateRequest(
            modelId: "cloud-model",
            route: "CopilotLight",
            providerRouteId: "cloud-fast",
            inputTokens: null,
            outputTokens: 20,
            isBillable: true,
            succeeded: true));

        var endpoint = CreateProvider(new CloudModelRouteOptions
        {
            Id = "cloud-fast",
            ModelId = "cloud-model",
        }).GetEndpoints(collector.GetModelUsage()).Single(item => item.Id == "cloud-fast");

        Assert.Null(endpoint.EstimatedCostUsd);
        Assert.Equal(0, endpoint.EstimatedCostRequests);
    }

    private static ModelEndpointProvider CreateProvider(params CloudModelRouteOptions[] models)
    {
        var backend = new FakeLocalBackend();
        var selected = new SelectedBackend();
        selected.Set(
            new LocalBackendSelection(
                backend,
                [new BackendProbe(backend.Kind, backend.Priority, BackendProbeResult.Available("ready"))]),
            [backend]);

        return new ModelEndpointProvider(
            selected,
            Options.Create(new GenieXOptions()),
            Options.Create(new CopilotSdkOptions
            {
                Enabled = true,
                Models = [.. models],
            }));
    }

    private static DashboardMetrics.RequestMetric CreateRequest(
        string modelId,
        string route,
        string? providerRouteId,
        long? inputTokens,
        long? outputTokens,
        bool isBillable,
        bool succeeded) =>
        new(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            route,
            modelId,
            providerRouteId,
            providerRouteId is null ? BackendKind.GenieXQairt.ToString() : BackendKind.Remote.ToString(),
            isBillable,
            IsFallback: false,
            succeeded,
            TimeToFirstTokenMs: 10,
            TotalDurationMs: 20,
            inputTokens,
            outputTokens,
            DecodeTokensPerSecond: null);

    private sealed class FakeLocalBackend : ILocalInferenceBackend
    {
        private readonly FakeChatClient _inner = new();

        public BackendKind Kind => BackendKind.GenieXQairt;

        public int Priority => 100;

        public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(BackendProbeResult.Available("ready"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }
}
