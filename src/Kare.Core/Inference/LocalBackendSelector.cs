using Kare.Abstractions;
using Microsoft.Extensions.Logging;

namespace Kare.Core.Inference;

/// <summary>
/// Probes every registered local backend once and selects the highest priority one
/// that is actually available.
/// A missing accelerator runtime is an expected outcome on a board without QNN or
/// QAIRT installed. It is logged and demoted, not treated as a fault.
/// </summary>
public sealed class LocalBackendSelector
{
    private readonly IReadOnlyList<ILocalInferenceBackend> _backends;
    private readonly ILogger<LocalBackendSelector> _logger;

    /// <summary>Creates the selector over all registered backends.</summary>
    public LocalBackendSelector(
        IEnumerable<ILocalInferenceBackend> backends,
        ILogger<LocalBackendSelector> logger)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(logger);
        _backends = [.. backends];
        _logger = logger;
    }

    /// <summary>
    /// Returns the selected backend and the probe results for every candidate.
    /// </summary>
    /// <param name="cancellationToken">Cancels probing.</param>
    /// <exception cref="NoBackendAvailableException">No backend passed its probe.</exception>
    public async ValueTask<LocalBackendSelection> SelectAsync(CancellationToken cancellationToken = default)
    {
        var ordered = _backends.OrderByDescending(static b => b.Priority).ToArray();
        var probes = new List<BackendProbe>(ordered.Length);
        ILocalInferenceBackend? selected = null;

        foreach (var backend in ordered)
        {
            var result = await backend.ProbeAsync(cancellationToken).ConfigureAwait(false);
            probes.Add(new BackendProbe(backend.Kind, backend.Priority, result));

            if (result.IsAvailable)
            {
                if (selected is null)
                {
                    selected = backend;
                    _logger.LogInformation(
                        "Selected local backend {Backend} at priority {Priority}. {Detail}",
                        backend.Kind,
                        backend.Priority,
                        result.Detail);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Local backend {Backend} at priority {Priority} is unavailable. {Detail}",
                    backend.Kind,
                    backend.Priority,
                    result.Detail);
            }
        }

        if (selected is null)
        {
            var detail = string.Join("; ", probes.Select(static p => $"{p.Kind}: {p.Result.Detail}"));
            throw new NoBackendAvailableException(detail);
        }

        return new LocalBackendSelection(selected, probes);
    }
}

/// <summary>
/// The chosen backend plus the full probe record, so a benchmark run can report which
/// paths were available and which were not.
/// </summary>
/// <param name="Backend">The selected backend.</param>
/// <param name="Probes">Every candidate's probe result, highest priority first.</param>
public sealed record LocalBackendSelection(
    ILocalInferenceBackend Backend,
    IReadOnlyList<BackendProbe> Probes);

/// <summary>One candidate backend's probe outcome.</summary>
/// <param name="Kind">The backend execution path.</param>
/// <param name="Priority">Its configured preference.</param>
/// <param name="Result">Whether it is usable, and why.</param>
public readonly record struct BackendProbe(
    BackendKind Kind,
    int Priority,
    BackendProbeResult Result);
