using Kare.Abstractions;
using Microsoft.Extensions.AI;

namespace Kare.Core.Routing;

/// <summary>
/// Sends every request to the local model.
///
/// This is Kare's starting policy on purpose. Until the board has been benchmarked there
/// is no measured basis for deciding what deserves a billable cloud call, and guessing
/// would spend Copilot credits for no reason. Cloud escalation is added as a separate
/// selector once the local quality and latency numbers exist.
/// </summary>
public sealed class LocalOnlyRouteSelector : IRouteSelector
{
    private readonly RouteDecision _decision;

    /// <summary>Creates the selector for a known local backend and model.</summary>
    /// <param name="backend">The execution path that will actually serve requests.</param>
    /// <param name="modelId">The local model identifier, used for accounting.</param>
    public LocalOnlyRouteSelector(BackendKind backend, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _decision = new RouteDecision(
            KareRoute.LocalSlm,
            "Local only policy. Cloud escalation is not configured.",
            modelId,
            backend,
            IsBillable: false);
    }

    /// <inheritdoc />
    public ValueTask<RouteDecision> SelectAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_decision);
    }
}
