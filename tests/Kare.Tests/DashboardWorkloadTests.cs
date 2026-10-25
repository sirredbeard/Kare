using Kare.Abstractions;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Service.Dashboard;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class DashboardWorkloadTests
{
    [Fact]
    public async Task RefreshWorkloadShowsInFlightWorkBeforeAnyRouteRecord()
    {
        var collector = new InMemoryMetricsCollector();
        var limits = new InferenceLimits { MaxConcurrentInference = 1 };
        using var gate = new InferenceGate(Options.Create(limits));
        var activity = new DashboardActivity(
            new NoOpRouteRecorder(),
            collector,
            gate,
            Options.Create(limits));

        // Hold the only inference slot, the way a running request does. No
        // route record exists yet, so a completion-only snapshot would read 0.
        using var lease = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        activity.RefreshWorkload();

        var workload = Assert.IsType<DashboardMetrics.WorkloadSnapshot>(collector.GetWorkload());
        Assert.Equal(1, workload.ActiveRequests);
        Assert.Equal(0, workload.QueueDepth);
        Assert.Equal(0, workload.TotalRequestsProcessed);
        Assert.Equal(100, workload.BusyTimePercent);
    }

    private sealed class NoOpRouteRecorder : IRouteRecorder
    {
        public ValueTask RecordAsync(
            RouteDecision decision,
            RouteUsage usage,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
