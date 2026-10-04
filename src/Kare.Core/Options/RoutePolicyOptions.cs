using System.ComponentModel.DataAnnotations;

namespace Kare.Core.Options;

/// <summary>Explicit local, Copilot, and Foundry routing policy.</summary>
public sealed class RoutePolicyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Routing";

    /// <summary>Wire model name that always selects the local SLM.</summary>
    [Required]
    public string LocalModelId { get; set; } = "kare-local";

    /// <summary>Wire model name that selects the low-cost Copilot model pool.</summary>
    [Required]
    public string LightModelId { get; set; } = "kare-fast";

    /// <summary>Wire model name that explicitly selects the configured heavy Copilot model.</summary>
    [Required]
    public string CloudModelId { get; set; } = "kare-copilot";

    /// <summary>Wire model name that explicitly selects a configured Foundry deployment.</summary>
    [Required]
    public string ComplexModelId { get; set; } = "kare-complex";

    /// <summary>Wire model name that applies the deterministic automatic policy.</summary>
    [Required]
    public string AutomaticModelId { get; set; } = "kare-auto";

    /// <summary>
    /// Enables automatic cloud selection for <see cref="AutomaticModelId"/>.
    /// This is off by default because cloud calls are billable.
    /// </summary>
    public bool EnableAutomaticCloudEscalation { get; set; }

    /// <summary>
    /// Character threshold above which the automatic route selects the light Copilot pool.
    /// </summary>
    [Range(1_024, 10_000_000)]
    public int ModeratePromptCharacterThreshold { get; set; } = 6_000;

    /// <summary>Character threshold above which automatic routing selects the complex route.</summary>
    [Range(1_024, 10_000_000)]
    public int ComplexPromptCharacterThreshold { get; set; } = 24_000;

}
