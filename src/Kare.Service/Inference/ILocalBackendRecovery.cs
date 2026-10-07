using Kare.Abstractions;

namespace Kare.Service.Inference;

/// <summary>Attempts one bounded recovery of an unhealthy accelerator sidecar.</summary>
public interface ILocalBackendRecovery
{
    bool Supports(BackendKind backend);

    Task<bool> TryRecoverAsync(
        BackendKind backend,
        CancellationToken cancellationToken = default);
}
