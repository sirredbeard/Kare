using Microsoft.Extensions.AI;

namespace Kare.Abstractions;

/// <summary>
/// A configured cloud escalation path. Cloud backends are separate from local backend
/// selection so an unavailable cloud account never prevents local startup.
/// </summary>
public interface ICloudInferenceBackend : IChatClient
{
    /// <summary>The route recorded when this backend serves a request.</summary>
    KareRoute Route { get; }

    /// <summary>The configured model identifier.</summary>
    string ModelId { get; }

    /// <summary>Checks credentials and runtime availability without sending a model request.</summary>
    ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}
