using Kare.Abstractions;
using Microsoft.Extensions.AI;

namespace Kare.Service;

/// <summary>
/// Holds the backend chosen at startup. Selection is an async probe, so it cannot run
/// inside a service factory, and Kare refuses to answer before it has happened.
/// </summary>
public sealed class SelectedBackend
{
    private ILocalInferenceBackend? _backend;

    /// <summary>The selected backend.</summary>
    public ILocalInferenceBackend Backend =>
        _backend ?? throw new InvalidOperationException("Backend selection has not completed.");

    /// <summary>The selected execution path.</summary>
    public BackendKind Kind => Backend.Kind;

    /// <summary>The model identifier reported for accounting.</summary>
    public string ModelId { get; private set; } = "unknown";

    /// <summary>Records the selection made at startup.</summary>
    public void Set(ILocalInferenceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        ModelId = backend.GetService<ChatClientMetadata>()?.DefaultModelId ?? backend.Kind.ToString();
    }
}
