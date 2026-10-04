using Kare.Abstractions;
using Kare.Core.Routing;

namespace Kare.Service.Dashboard;

/// <summary>
/// Bridges MetricsRouteRecorder output to IDashboardMetricsCollector.
/// Registers as a singleton that observes all routing decisions and records them for dashboard display.
/// Runs alongside the existing OpenTelemetry metrics pipeline without modification.
/// </summary>
public sealed class DashboardActivity : IRouteRecorder, IDisposable
{
    private readonly IRouteRecorder _inner;
    private readonly IDashboardMetricsCollector _collector;

    public DashboardActivity(IRouteRecorder inner, IDashboardMetricsCollector collector)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(collector);
        _inner = inner;
        _collector = collector;
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
            Backend: decision.Backend.ToString(),
            IsBillable: decision.IsBillable,
            IsFallback: decision.IsFallback,
            Succeeded: usage.Succeeded,
            TimeToFirstTokenMs: usage.TimeToFirstToken.TotalMilliseconds,
            TotalDurationMs: usage.TotalDuration.TotalMilliseconds,
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
    }

    public void Dispose()
    {
        if (_inner is IDisposable d)
            d.Dispose();
    }
}
