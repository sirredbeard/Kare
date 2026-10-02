using System.ComponentModel.DataAnnotations;

namespace Kare.Inference.GenieX;

/// <summary>Configuration for the loopback GenieX OpenAI-compatible server.</summary>
public sealed class GenieXOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Inference:GenieX";

    /// <summary>Whether Kare should probe and select the GenieX backend.</summary>
    public bool Enabled { get; set; }

    /// <summary>Loopback OpenAI-compatible endpoint exposed by <c>geniex serve</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:18181/v1");

    /// <summary>
    /// Model name sent to GenieX. GenieX v0.7.1 lists a precision suffix but rejects
    /// that suffix on requests, so this must be the base model name.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelId { get; set; } = "qualcomm/qwen3_1_7b";

    /// <summary>Backend preference. This should stay above the CPU fallback.</summary>
    [Range(0, 1000)]
    public int Priority { get; set; } = 100;

    /// <summary>Timeout for the availability probe, not generation.</summary>
    [Range(1, 60)]
    public int ProbeTimeoutSeconds { get; set; } = 5;
}
