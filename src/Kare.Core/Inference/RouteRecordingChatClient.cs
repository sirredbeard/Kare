using System.Diagnostics;
using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Kare.Core.Inference;

/// <summary>
/// Measures and records the route taken for every request.
/// Kare treats "which route served this and what did it cost" as a product requirement,
/// not as optional telemetry, so this wrapper is always installed.
/// </summary>
public sealed class RouteRecordingChatClient : DelegatingChatClient
{
    private readonly IRouteRecorder _recorder;
    private readonly ILogger<RouteRecordingChatClient> _logger;
    private readonly RouteDecision _decision;

    /// <summary>Creates the recording client for a fixed route.</summary>
    public RouteRecordingChatClient(
        IChatClient innerClient,
        RouteDecision decision,
        IRouteRecorder recorder,
        ILogger<RouteRecordingChatClient> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(logger);
        _decision = decision;
        _recorder = recorder;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(start, start, null, null, succeeded: false, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var end = Stopwatch.GetTimestamp();

        // A non streaming call has no separate first token moment, so first token and
        // total are the same measurement. Reporting them as different would be invented data.
        await RecordAsync(
            start,
            end,
            firstTokenTimestamp: end,
            usage: response.Usage,
            succeeded: true,
            cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        long? firstToken = null;
        UsageDetails? usage = null;
        var completed = false;

        try
        {
            var stream = base.GetStreamingResponseAsync(messages, options, cancellationToken);
            await foreach (var update in stream.ConfigureAwait(false))
            {
                firstToken ??= Stopwatch.GetTimestamp();

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        usage = usageContent.Details;
                    }
                }

                yield return update;
            }

            completed = true;
        }
        finally
        {
            await RecordAsync(
                start,
                Stopwatch.GetTimestamp(),
                firstToken,
                usage,
                succeeded: completed,
                // The caller's token may already be cancelled. Recording must still happen,
                // otherwise a cancelled request disappears from the accounting.
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask RecordAsync(
        long start,
        long end,
        long? firstTokenTimestamp,
        UsageDetails? usage,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.GetElapsedTime(start, end);
        var timeToFirstToken = firstTokenTimestamp is { } first
            ? Stopwatch.GetElapsedTime(start, first)
            : total;

        var measured = new RouteUsage(
            timeToFirstToken,
            total,
            usage?.InputTokenCount,
            usage?.OutputTokenCount,
            succeeded);

        LogRoute(measured);

        await _recorder.RecordAsync(_decision, measured, cancellationToken).ConfigureAwait(false);
    }

    private void LogRoute(RouteUsage usage)
    {
        if (_decision.IsFallback)
        {
            _logger.LogWarning(
                "Route {Route} served the request after falling back from {FellBackFrom}. Reason: {Reason}. Backend: {Backend}.",
                _decision.Route,
                _decision.FellBackFrom,
                _decision.Reason,
                _decision.Backend);
        }

        _logger.LogInformation(
            "Route {Route} backend {Backend} model {ModelId} billable {IsBillable} ttft {TimeToFirstTokenMs}ms total {TotalMs}ms out {OutputTokens} succeeded {Succeeded}.",
            _decision.Route,
            _decision.Backend,
            _decision.ModelId,
            _decision.IsBillable,
            (long)usage.TimeToFirstToken.TotalMilliseconds,
            (long)usage.TotalDuration.TotalMilliseconds,
            usage.OutputTokens,
            usage.Succeeded);
    }
}
