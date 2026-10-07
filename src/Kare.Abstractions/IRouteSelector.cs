using Microsoft.Extensions.AI;

namespace Kare.Abstractions;

/// <summary>
/// Chooses the route for a request before any model call is made.
/// Implementations read configured policy only. They must not call a billable
/// provider to decide where to send a request.
/// </summary>
public interface IRouteSelector
{
    /// <summary>
    /// Selects a route for the supplied conversation.
    /// </summary>
    /// <param name="messages">The conversation to serve.</param>
    /// <param name="options">Caller supplied chat options, if any.</param>
    /// <param name="cancellationToken">Cancels selection.</param>
    ValueTask<RouteDecision> SelectAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken = default);
}
