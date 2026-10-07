using Kare.Abstractions;
using Kare.Core.Inference;
using Microsoft.Extensions.AI;

namespace Kare.Service;

/// <summary>
/// Holds the preferred local backend and a validated CPU fallback. Selection is an
/// async probe, so it cannot run inside a service factory.
/// </summary>
public sealed class SelectedBackend : ILocalBackendStatus
{
    private readonly object _sync = new();
    private ILocalInferenceBackend? _preferred;
    private ILocalInferenceBackend? _fallback;
    private ILocalInferenceBackend? _active;
    private string? _degradedReason;
    private bool _warningPending;

    /// <summary>The backend currently serving local requests.</summary>
    public ILocalInferenceBackend Backend
    {
        get
        {
            lock (_sync)
            {
                return _active ??
                    throw new InvalidOperationException("Backend selection has not completed.");
            }
        }
    }

    /// <summary>The accelerator backend Kare should return to after recovery.</summary>
    public ILocalInferenceBackend PreferredBackend
    {
        get
        {
            lock (_sync)
            {
                return _preferred ??
                    throw new InvalidOperationException("Backend selection has not completed.");
            }
        }
    }

    /// <inheritdoc />
    public BackendKind Kind => Backend.Kind;

    /// <inheritdoc />
    public string ModelId =>
        Backend.GetService<ChatClientMetadata>()?.DefaultModelId ?? Backend.Kind.ToString();

    /// <inheritdoc />
    public bool IsDegraded
    {
        get
        {
            lock (_sync)
            {
                return _degradedReason is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsCpuFallback
    {
        get
        {
            lock (_sync)
            {
                return _degradedReason is not null &&
                    _active?.Kind == BackendKind.OnnxGenAiCpu;
            }
        }
    }

    /// <inheritdoc />
    public string? DegradedReason
    {
        get
        {
            lock (_sync)
            {
                return _degradedReason;
            }
        }
    }

    /// <summary>Records the selection made at startup.</summary>
    public void Set(
        LocalBackendSelection selection,
        IEnumerable<ILocalInferenceBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(backends);

        var candidates = backends.OrderByDescending(static backend => backend.Priority).ToArray();
        var preferred = candidates.FirstOrDefault(static backend =>
            backend.Kind != BackendKind.OnnxGenAiCpu) ?? selection.Backend;
        var fallback = candidates.FirstOrDefault(backend =>
            backend.Kind == BackendKind.OnnxGenAiCpu &&
            selection.Probes.Any(probe =>
                probe.Kind == backend.Kind &&
                probe.Priority == backend.Priority &&
                probe.Result.IsAvailable));

        lock (_sync)
        {
            _preferred = preferred;
            _fallback = fallback;
            _active = selection.Backend;

            if (!ReferenceEquals(selection.Backend, preferred))
            {
                _degradedReason = selection.Probes.FirstOrDefault(probe =>
                    probe.Kind == preferred.Kind &&
                    probe.Priority == preferred.Priority).Result.Detail;
                _warningPending = true;
            }
        }
    }

    /// <summary>Moves local requests to the validated CPU path, when one exists.</summary>
    public bool MarkDegraded(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_sync)
        {
            var changed = _degradedReason is null;
            _degradedReason = reason;
            _warningPending |= changed;
            if (_fallback is not null)
            {
                _active = _fallback;
                return true;
            }

            return false;
        }
    }

    /// <summary>Returns local requests to the preferred accelerator.</summary>
    public void MarkRecovered()
    {
        lock (_sync)
        {
            _active = _preferred ??
                throw new InvalidOperationException("Backend selection has not completed.");
            _degradedReason = null;
            _warningPending = false;
        }
    }

    /// <summary>Returns the one user-visible notice for the current degradation episode.</summary>
    public bool TryTakeWarning(out string warning)
    {
        lock (_sync)
        {
            if (!_warningPending || _degradedReason is null)
            {
                warning = string.Empty;
                return false;
            }

            _warningPending = false;
            warning = _active?.Kind == BackendKind.OnnxGenAiCpu
                ? "! Kare's local NPU is unavailable. Local routing is using the CPU fallback until FastRPC recovers."
                : "! Kare's local NPU is unavailable, and no CPU fallback is configured. This request may use a configured cloud route.";
            return true;
        }
    }
}
