using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kare.Abstractions;

namespace Kare.Core.Routing;

/// <summary>
/// Default recorder. Emits OpenTelemetry compatible metrics and keeps no prompt or
/// completion text. The durable recorder backed by PostgreSQL replaces this once the
/// persistence stage lands, and the metric names stay the same.
/// </summary>
public sealed class MetricsRouteRecorder : IRouteRecorder, IDisposable
{
    /// <summary>Meter name for Kare routing metrics.</summary>
    public const string MeterName = "Kare.Routing";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _timeToFirstToken;
    private readonly Histogram<double> _totalDuration;
    private readonly Histogram<double> _decodeRate;
    private readonly Counter<long> _outputTokens;

    /// <summary>Creates the recorder.</summary>
    public MetricsRouteRecorder(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);

        _requests = _meter.CreateCounter<long>(
            "kare.route.requests",
            unit: "{request}",
            description: "Requests served, by route and backend.");

        _timeToFirstToken = _meter.CreateHistogram<double>(
            "kare.route.time_to_first_token",
            unit: "ms",
            description: "Time from send to first streamed token.");

        _totalDuration = _meter.CreateHistogram<double>(
            "kare.route.duration",
            unit: "ms",
            description: "Time from send to final streamed token.");

        _decodeRate = _meter.CreateHistogram<double>(
            "kare.route.decode_rate",
            unit: "{token}/s",
            description: "Steady state decode rate, excluding prompt processing.");

        _outputTokens = _meter.CreateCounter<long>(
            "kare.route.output_tokens",
            unit: "{token}",
            description: "Tokens generated, by route and backend.");
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(
        RouteDecision decision,
        RouteUsage usage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tags = new TagList
        {
            { "kare.route", decision.Route.ToString() },
            { "kare.backend", decision.Backend.ToString() },
            { "kare.model", decision.ModelId },
            { "kare.billable", decision.IsBillable },
            { "kare.fallback", decision.IsFallback },
            { "kare.succeeded", usage.Succeeded },
        };

        _requests.Add(1, tags);
        _timeToFirstToken.Record(usage.TimeToFirstToken.TotalMilliseconds, tags);
        _totalDuration.Record(usage.TotalDuration.TotalMilliseconds, tags);

        if (usage.OutputTokens is { } output)
        {
            _outputTokens.Add(output, tags);
        }

        if (usage.DecodeTokensPerSecond is { } rate)
        {
            _decodeRate.Record(rate, tags);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
