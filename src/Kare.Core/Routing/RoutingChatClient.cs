using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Kare.Core.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Kare.Core.Routing;

/// <summary>Dispatches each request to the route selected by <see cref="IRouteSelector"/>.</summary>
public sealed class KareRoutingChatClient : IChatClient
{
    private readonly IChatClient _local;
    private readonly ICloudInferenceBackend _cloud;
    private readonly IRouteSelector _selector;
    private readonly IRouteRecorder _recorder;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the routing client.</summary>
    public KareRoutingChatClient(
        IChatClient local,
        ICloudInferenceBackend cloud,
        IRouteSelector selector,
        IRouteRecorder recorder,
        ILoggerFactory loggerFactory)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var decision = await _selector.SelectAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        var client = await ResolveClientAsync(decision, cancellationToken).ConfigureAwait(false);
        var recording = CreateRecordingClient(client, decision);
        return await recording.GetResponseAsync(
            materialized,
            ApplySelectedModel(options, decision),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var decision = await _selector.SelectAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        var client = await ResolveClientAsync(decision, cancellationToken).ConfigureAwait(false);
        var recording = CreateRecordingClient(client, decision);

        await foreach (var update in recording
            .GetStreamingResponseAsync(
                materialized,
                ApplySelectedModel(options, decision),
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private async ValueTask<IChatClient> ResolveClientAsync(
        RouteDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Backend != BackendKind.Remote)
        {
            return _local;
        }

        var probe = await _cloud.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsAvailable)
        {
            throw new NoBackendAvailableException($"{decision.Route}: {probe.Detail}");
        }

        return _cloud;
    }

    private RouteRecordingChatClient CreateRecordingClient(IChatClient client, RouteDecision decision) =>
        new(
            client,
            decision,
            _recorder,
            _loggerFactory.CreateLogger<RouteRecordingChatClient>());

    private static ChatOptions ApplySelectedModel(
        ChatOptions? options,
        RouteDecision decision)
    {
        var selected = options?.Clone() ?? new ChatOptions();
        selected.ModelId = decision.ProviderRouteId ?? decision.ModelId;
        return selected;
    }
}
