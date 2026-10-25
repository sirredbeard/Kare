using Kare.Abstractions;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Core.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kare.Service.Dashboard;

/// <summary>
/// Bridges MetricsRouteRecorder output to IDashboardMetricsCollector.
/// Registers as a singleton that observes all routing decisions and records them for dashboard display.
/// Runs alongside the existing OpenTelemetry metrics pipeline without modification.
/// </summary>
public sealed class DashboardActivity : IRouteRecorder
{
    private readonly IRouteRecorder _inner;
    private readonly IDashboardMetricsCollector _collector;
    private readonly InferenceGate _gate;
    private readonly int _maximumConcurrency;
    private readonly Lock _sync = new();
    private long _totalRequests;
    private double _totalTimeToFirstTokenMs;

    public DashboardActivity(
        IRouteRecorder inner,
        IDashboardMetricsCollector collector,
        InferenceGate gate,
        IOptions<InferenceLimits> limits)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(limits);
        _inner = inner;
        _collector = collector;
        _gate = gate;
        _maximumConcurrency = limits.Value.MaxConcurrentInference;
    }

    /// <summary>
    /// Record a routing decision and forward to both OpenTelemetry and dashboard metrics.
    /// </summary>
    public async ValueTask RecordAsync(
        RouteDecision decision,
        RouteUsage usage,
        CancellationToken cancellationToken = default)
    {
        // Forward to OpenTelemetry metrics pipeline first
        await _inner.RecordAsync(decision, usage, cancellationToken).ConfigureAwait(false);

        // Record to dashboard collector for real-time display
        var request = new DashboardMetrics.RequestMetric(
            Id: Guid.NewGuid().ToString("N"),
            Timestamp: DateTime.UtcNow,
            Route: decision.Route.ToString(),
            ModelId: decision.ModelId,
            ProviderRouteId: decision.ProviderRouteId,
            Backend: decision.Backend.ToString(),
            IsBillable: decision.IsBillable,
            IsFallback: decision.IsFallback,
            Succeeded: usage.Succeeded,
            TimeToFirstTokenMs: usage.TimeToFirstToken.TotalMilliseconds,
            TotalDurationMs: usage.TotalDuration.TotalMilliseconds,
            InputTokens: usage.InputTokens,
            OutputTokens: usage.OutputTokens,
            DecodeTokensPerSecond: usage.DecodeTokensPerSecond
        );

        _collector.RecordRequest(request);

        var activity = new DashboardMetrics.Activity(
            Id: Guid.NewGuid().ToString("N"),
            Timestamp: DateTime.UtcNow,
            Type: decision.IsFallback ? "fallback" : "route",
            Description: decision.Reason,
            Status: usage.Succeeded ? "succeeded" : "failed",
            Route: decision.Route.ToString(),
            Backend: decision.Backend.ToString()
        );

        _collector.RecordActivity(activity);

        lock (_sync)
        {
            _totalRequests++;
            _totalTimeToFirstTokenMs += usage.TimeToFirstToken.TotalMilliseconds;
        }

        RefreshWorkload();
    }

    /// <summary>
    /// Publishes a workload snapshot from the live gate state. The sampler calls
    /// this while requests are still running, so the dashboard does not sit at
    /// zero for the whole duration of a long inference and then jump at the end.
    /// </summary>
    public void RefreshWorkload()
    {
        var activeRequests = Math.Max(0, _maximumConcurrency - _gate.AvailableSlots);
        long totalRequests;
        double averageTimeToFirstTokenMs;
        lock (_sync)
        {
            totalRequests = _totalRequests;
            averageTimeToFirstTokenMs = totalRequests == 0
                ? 0
                : _totalTimeToFirstTokenMs / totalRequests;
        }

        _collector.UpdateWorkload(new DashboardMetrics.WorkloadSnapshot(
            QueueDepth: _gate.Waiting,
            ActiveRequests: activeRequests,
            BusyTimePercent: _maximumConcurrency == 0
                ? 0
                : activeRequests * 100.0 / _maximumConcurrency,
            TotalRequestsProcessed: totalRequests,
            AverageTimeToFirstTokenMs: averageTimeToFirstTokenMs));
    }
}

/// <summary>
/// Samples the inference gate on a fixed interval and republishes the workload
/// snapshot, so active and queued work is visible while it is running instead
/// of only after a route record lands.
/// </summary>
public sealed class WorkloadSampler(DashboardActivity activity) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            activity.RefreshWorkload();
        }
    }
}
