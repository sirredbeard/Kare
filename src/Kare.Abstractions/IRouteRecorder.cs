namespace Kare.Abstractions;

/// <summary>
/// Records the route actually taken, plus the measured cost of taking it.
/// Kare uses this for accounting, for benchmark output, and for the budget policy.
/// Implementations must not persist prompt text, completion text, or secrets.
/// </summary>
public interface IRouteRecorder
{
    /// <summary>
    /// Records one completed request.
    /// </summary>
    /// <param name="decision">The route that served the request.</param>
    /// <param name="usage">Measured latency and token counts.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RecordAsync(
        RouteDecision decision,
        RouteUsage usage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Measured cost of one served request.
/// </summary>
/// <param name="TimeToFirstToken">Time from send to the first streamed token.</param>
/// <param name="TotalDuration">Time from send to the final streamed token.</param>
/// <param name="InputTokens">Tokens accepted as input, when the backend reports them.</param>
/// <param name="OutputTokens">Tokens generated, when the backend reports them.</param>
/// <param name="Succeeded">False when the request failed or was cancelled.</param>
public readonly record struct RouteUsage(
    TimeSpan TimeToFirstToken,
    TimeSpan TotalDuration,
    long? InputTokens,
    long? OutputTokens,
    bool Succeeded)
{
    /// <summary>
    /// Steady state decode rate in tokens per second, or null when it cannot be derived.
    /// Excludes prompt processing by subtracting time to first token.
    /// </summary>
    public double? DecodeTokensPerSecond
    {
        get
        {
            if (OutputTokens is not > 1)
            {
                return null;
            }

            var decode = TotalDuration - TimeToFirstToken;
            return decode > TimeSpan.Zero
                ? (OutputTokens.Value - 1) / decode.TotalSeconds
                : null;
        }
    }
}
