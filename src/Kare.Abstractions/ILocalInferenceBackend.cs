using Microsoft.Extensions.AI;

namespace Kare.Abstractions;

/// <summary>
/// A local inference path on the device. Kare registers one of these per backend
/// and selects between them at startup using <see cref="ProbeAsync"/>.
/// </summary>
public interface ILocalInferenceBackend : IChatClient
{
    /// <summary>The execution path this backend actually uses.</summary>
    BackendKind Kind { get; }

    /// <summary>
    /// Preference order when several backends are available. Higher wins.
    /// CPU must stay registered at the lowest priority so it remains the fallback.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Checks whether this backend can run on the current machine. A failed probe
    /// is an expected result on a board without the accelerator runtime, not an error.
    /// </summary>
    ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of a backend availability probe.
/// </summary>
/// <param name="IsAvailable">True when the backend can serve requests.</param>
/// <param name="Detail">
/// Why the backend is available or unavailable, such as a missing native library name.
/// Must not contain prompt contents or secrets.
/// </param>
public readonly record struct BackendProbeResult(bool IsAvailable, string Detail)
{
    /// <summary>Creates an available result.</summary>
    public static BackendProbeResult Available(string detail) => new(true, detail);

    /// <summary>Creates an unavailable result.</summary>
    public static BackendProbeResult Unavailable(string detail) => new(false, detail);
}
