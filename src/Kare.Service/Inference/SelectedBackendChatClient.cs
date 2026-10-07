using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Microsoft.Extensions.AI;

namespace Kare.Service.Inference;

/// <summary>Dispatches each local request through the current health-selected backend.</summary>
public sealed class SelectedBackendChatClient : IChatClient
{
    private readonly SelectedBackend _selected;
    private readonly LocalBackendHealthMonitor _monitor;
    private readonly ILogger<SelectedBackendChatClient> _logger;

    public SelectedBackendChatClient(
        SelectedBackend selected,
        LocalBackendHealthMonitor monitor,
        ILogger<SelectedBackendChatClient> logger)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var backend = _selected.Backend;

        try
        {
            var response = await backend.GetResponseAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false);
            if (_selected.IsDegraded && backend.Kind != BackendKind.OnnxGenAiCpu)
            {
                _selected.MarkRecovered();
                _logger.LogInformation(
                    "Local backend {Backend} completed a real request and cleared its degraded state.",
                    backend.Kind);
            }

            return response;
        }
        catch (LocalInferenceException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (backend.Kind == BackendKind.OnnxGenAiCpu ||
                !_selected.MarkDegraded(SafeReason(backend.Kind)))
            {
                _monitor.RequestCheck();
                throw;
            }

            _logger.LogWarning(
                ex,
                "Local backend {Backend} failed. Retrying once on the configured CPU fallback.",
                backend.Kind);
            _monitor.RequestCheck();
            return await _selected.Backend
                .GetResponseAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var backend = _selected.Backend;
        var emitted = false;
        var retried = false;
        var enumerator = backend
            .GetStreamingResponseAsync(materialized, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (LocalInferenceException ex) when (
                    !emitted &&
                    !retried &&
                    !cancellationToken.IsCancellationRequested &&
                    backend.Kind != BackendKind.OnnxGenAiCpu &&
                    _selected.MarkDegraded(SafeReason(backend.Kind)))
                {
                    _logger.LogWarning(
                        ex,
                        "Local backend {Backend} failed before streaming. Retrying once on the configured CPU fallback.",
                        backend.Kind);
                    _monitor.RequestCheck();
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                    retried = true;
                    backend = _selected.Backend;
                    enumerator = backend
                        .GetStreamingResponseAsync(materialized, options, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                    continue;
                }

                if (!moved)
                {
                    if (_selected.IsDegraded && backend.Kind != BackendKind.OnnxGenAiCpu)
                    {
                        _selected.MarkRecovered();
                        _logger.LogInformation(
                            "Local backend {Backend} completed a real stream and cleared its degraded state.",
                            backend.Kind);
                    }

                    yield break;
                }

                emitted = true;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType.IsInstanceOfType(_selected))
        {
            return _selected;
        }

        return _selected.Backend.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
    }

    private static string SafeReason(BackendKind backend) =>
        $"{backend} failed a local inference request. Raw provider details were withheld.";
}
