using Kare.Abstractions;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Service.Api;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class DashboardTests
{
    [Fact]
    public async Task ActivityRecordsRouteWithoutInventingUnknownUsage()
    {
        var collector = new InMemoryMetricsCollector();
        var limits = new InferenceLimits { MaxConcurrentInference = 1 };
        using var gate = new InferenceGate(Options.Create(limits));
        var activity = new DashboardActivity(
            new CapturingRouteRecorder(),
            collector,
            gate,
            Options.Create(limits));

        await activity.RecordAsync(
            new RouteDecision(
                KareRoute.LocalSlm,
                "test route",
                "test-model",
                BackendKind.OnnxGenAiCpu,
                IsBillable: false),
            new RouteUsage(
                TimeSpan.FromMilliseconds(120),
                TimeSpan.FromMilliseconds(300),
                InputTokens: null,
                OutputTokens: null,
                Succeeded: true),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(collector.GetRequests());
        Assert.Null(request.OutputTokens);
        Assert.Null(request.DecodeTokensPerSecond);
        Assert.Equal("LocalSlm", request.Route);

        var workload = Assert.IsType<DashboardMetrics.WorkloadSnapshot>(collector.GetWorkload());
        Assert.Equal(1, workload.TotalRequestsProcessed);
        Assert.Equal(120, workload.AverageTimeToFirstTokenMs);
    }

    [Fact]
    public void RegistryEntriesAreUpsertedByStableIdentity()
    {
        var collector = new InMemoryMetricsCollector();
        var created = DateTime.UtcNow.AddMinutes(-1);

        collector.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            "key",
            created,
            LastAccessedAt: null,
            SizeBytes: 10,
            "application/json"));
        collector.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            "key",
            DateTime.UtcNow,
            DateTime.UtcNow,
            SizeBytes: 20,
            "application/json"));

        var entry = Assert.Single(collector.GetCacheEntries());
        Assert.Equal(created, entry.CreatedAt);
        Assert.Equal(20, entry.SizeBytes);
        Assert.NotNull(entry.LastAccessedAt);
    }

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/dashboard/api/snapshot")]
    public async Task ApiKeyProtectsModelAndDashboardData(string path)
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new KareServiceOptions { ApiKey = "secret" }));
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private sealed class CapturingRouteRecorder : IRouteRecorder
    {
        public ValueTask RecordAsync(
            RouteDecision decision,
            RouteUsage usage,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
