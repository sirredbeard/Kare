using System.ComponentModel.DataAnnotations;

namespace Kare.Service.Options;

/// <summary>
/// Service level settings. These control who may call Kare and what it advertises,
/// not how inference is bounded. Inference bounds live in
/// <see cref="Kare.Core.Options.InferenceLimits"/>.
/// </summary>
public sealed class KareServiceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Service";

    /// <summary>
    /// The model identifier Kare advertises on <c>/v1/models</c> and echoes in responses.
    /// Copilot CLI BYOK sends a model name, and Kare answers for whatever it is configured
    /// to serve rather than pretending to host a catalogue.
    /// </summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string ModelId { get; set; } = "kare-local";

    /// <summary>
    /// Shared secret required in the <c>Authorization: Bearer</c> header.
    /// Leave empty only when the listener is bound to loopback. Kare refuses to start
    /// with an empty key once <see cref="AllowNonLoopbackBinding"/> is set.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Allows binding to an address other than loopback. This stays false until the
    /// plan's authentication, rate limit, and allow-list work is done. Kare must never
    /// be reachable from the public internet.
    /// </summary>
    public bool AllowNonLoopbackBinding { get; set; }

    /// <summary>
    /// Maximum request body size in bytes. A second line of defence in front of the
    /// prompt character bound, applied before the body is read.
    /// </summary>
    [Range(4_096, 64 * 1024 * 1024)]
    public int MaxRequestBodyBytes { get; set; } = 8 * 1024 * 1024;
}
