using System.ComponentModel.DataAnnotations;

namespace Kare.Core.Options;

/// <summary>
/// Hard bounds on local inference. The VENTUNO Q has 16 GB of shared memory and a
/// fixed thermal budget, so every one of these is a real limit and not a hint.
/// </summary>
public sealed class InferenceLimits
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Inference:Limits";

    /// <summary>
    /// Maximum characters accepted across all prompt messages. This is a cheap
    /// deterministic gateway bound. The backend still enforces the real token limit
    /// because characters per token varies by tokenizer and by language.
    /// </summary>
    [Range(1024, 4_000_000)]
    public int MaxPromptCharacters { get; set; } = 240_000;

    /// <summary>
    /// Maximum prompt tokens passed to the model. Starts at the 4K context the plan
    /// calls for. Raise only after the device measures 8K and 16K.
    /// </summary>
    [Range(256, 131_072)]
    public int MaxPromptTokens { get; set; } = 3_072;

    /// <summary>Maximum tokens the model may generate for one request.</summary>
    [Range(16, 32_768)]
    public int MaxOutputTokens { get; set; } = 1_024;

    /// <summary>
    /// Requests allowed to run inference at the same time. The board has one NPU and
    /// eight CPU cores shared with the service and the database, so this starts at one.
    /// </summary>
    [Range(1, 32)]
    public int MaxConcurrentInference { get; set; } = 1;

    /// <summary>
    /// Requests allowed to wait for an inference slot. Beyond this the gateway rejects
    /// rather than queueing without bound.
    /// </summary>
    [Range(0, 1024)]
    public int MaxQueueDepth { get; set; } = 8;

    // Bounds below are integers rather than TimeSpan on purpose. The TimeSpan overload of
    // RangeAttribute goes through a reflection based TypeConverter, which the trim and AOT
    // analyzers reject. Kare must stay Native AOT viable, so the seconds are the configured
    // surface and TimeSpan is derived.

    /// <summary>How long a request may wait for an inference slot before it is rejected.</summary>
    [Range(1, 600)]
    public int QueueTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Wall clock budget for one generation. Protects against a run that never reaches
    /// a stop token while the board is thermally throttled.
    /// </summary>
    [Range(1, 3600)]
    public int GenerationTimeoutSeconds { get; set; } = 300;

    /// <summary>How long a request may wait for an inference slot.</summary>
    public TimeSpan QueueTimeout => TimeSpan.FromSeconds(QueueTimeoutSeconds);

    /// <summary>Wall clock budget for one generation.</summary>
    public TimeSpan GenerationTimeout => TimeSpan.FromSeconds(GenerationTimeoutSeconds);
}
