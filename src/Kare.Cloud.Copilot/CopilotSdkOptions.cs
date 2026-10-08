using System.ComponentModel.DataAnnotations;
using Kare.Abstractions;

namespace Kare.Cloud.Copilot;

/// <summary>Configuration for bounded GitHub Copilot SDK escalation.</summary>
public sealed class CopilotSdkOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Cloud:CopilotSdk";

    /// <summary>Enables the cloud backend. Local inference remains available when disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Use GitHub Copilot or a direct Microsoft Foundry BYOK provider.</summary>
    public CopilotCloudProvider Provider { get; set; } = CopilotCloudProvider.GitHubCopilot;

    /// <summary>Model selected for the SDK session. GitHub Auto V2 uses <c>auto</c>.</summary>
    [Required]
    public string ModelId { get; set; } = "auto";

    /// <summary>Auto V2 routing tier used when <see cref="ModelId"/> is <c>auto</c>.</summary>
    [Required]
    public string AutoTier { get; set; } = "intelligence";

    /// <summary>
    /// Optional soft GitHub AI credit ceiling for one escalated request. Leave unset to use
    /// the caller's normal account and model policy without adding a Kare session ceiling.
    /// </summary>
    [Range(30, 10_000)]
    public double? MaxAiCreditsPerRequest { get; set; }

    /// <summary>Hard wall-clock timeout for one SDK request.</summary>
    [Range(5, 1800)]
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Private directory for SDK state. Keep it outside the repository.</summary>
    [Required]
    public string StateDirectory { get; set; } = "/var/lib/kare/copilot";

    /// <summary>Whether the SDK may use an existing local GitHub login.</summary>
    public bool UseLoggedInUser { get; set; }

    /// <summary>Environment variable containing a GitHub token when not using local login.</summary>
    [Required]
    public string GitHubTokenEnvironmentVariable { get; set; } = "KARE_COPILOT_GITHUB_TOKEN";

    /// <summary>Microsoft Foundry OpenAI-compatible base URL.</summary>
    public string? FoundryBaseUrl { get; set; }

    /// <summary>Environment variable containing the Foundry API key.</summary>
    [Required]
    public string FoundryApiKeyEnvironmentVariable { get; set; } = "KARE_FOUNDRY_API_KEY";

    /// <summary>Wire API for the legacy Foundry route.</summary>
    [Required]
    public string FoundryWireApi { get; set; } = "responses";

    /// <summary>Timeout for external credential acquisition commands.</summary>
    [Range(5, 120)]
    public int CredentialTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Provider-neutral model catalogue. Entries may target GitHub Copilot or Microsoft
    /// Foundry and are selected by tier and capability rather than provider name.
    /// </summary>
    public List<CloudModelRouteOptions> Models { get; set; } = [];
}

/// <summary>One model in Kare's provider-neutral cloud catalogue.</summary>
public sealed class CloudModelRouteOptions
{
    /// <summary>Stable route identifier used internally for dispatch.</summary>
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary>GitHub Copilot or Microsoft Foundry.</summary>
    public Kare.Abstractions.CloudModelProvider Provider { get; set; }

    /// <summary>Well-known model identifier used for model behavior and accounting.</summary>
    [Required]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Provider deployment or model name sent on the wire.</summary>
    public string? WireModel { get; set; }

    /// <summary>Escalation tier used by deterministic routing.</summary>
    public Kare.Abstractions.CloudModelTier Tier { get; set; }

    /// <summary>Lower values are preferred within a tier.</summary>
    [Range(0, 10_000)]
    public int Priority { get; set; }

    /// <summary>Whether this route supports caller-owned tool declarations.</summary>
    public bool SupportsTools { get; set; } = true;

    /// <summary>Whether this route accepts image attachments.</summary>
    public bool SupportsImages { get; set; }

    /// <summary>
    /// Provider boundaries allowed to receive this route. An empty list is invalid.
    /// </summary>
    public List<RouteMode> AllowedModes { get; set; } = [RouteMode.Personal, RouteMode.Work];

    /// <summary>OpenAI-compatible HTTPS base URL. Required only for Foundry.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Authentication method for this route.</summary>
    public CloudAuthentication Authentication { get; set; } = CloudAuthentication.Copilot;

    /// <summary>Environment variable containing the provider credential.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>OAuth scope used by Azure CLI token acquisition.</summary>
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";

    /// <summary>Provider wire API: <c>responses</c> or <c>anthropic</c>.</summary>
    public string WireApi { get; set; } = "responses";

    /// <summary>Maximum provider prompt tokens when known.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxPromptTokens { get; set; }

    /// <summary>Maximum provider output tokens when known.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxOutputTokens { get; set; }

    /// <summary>Average advertised input cost in USD per million tokens.</summary>
    public decimal? AverageInputCostUsdPerMillionTokens { get; set; }

    /// <summary>Average advertised output cost in USD per million tokens.</summary>
    public decimal? AverageOutputCostUsdPerMillionTokens { get; set; }

    /// <summary>Public source for the configured average model prices.</summary>
    public string? PricingSource { get; set; }

    /// <summary>Date the configured average model prices were checked.</summary>
    public DateOnly? PricingAsOf { get; set; }
}

/// <summary>Credential source used by a cloud model route.</summary>
public enum CloudAuthentication
{
    /// <summary>Use the GitHub Copilot session credential.</summary>
    Copilot = 0,

    /// <summary>Read a provider API key from the configured environment variable.</summary>
    EnvironmentApiKey = 1,

    /// <summary>Acquire a scoped Entra bearer token through Azure CLI.</summary>
    AzureCli = 2,
}

/// <summary>Cloud provider used by the Copilot SDK backend.</summary>
public enum CopilotCloudProvider
{
    /// <summary>GitHub Copilot API with the user's Copilot entitlement.</summary>
    GitHubCopilot = 0,

    /// <summary>Direct Microsoft Foundry BYOK through the Copilot SDK runtime.</summary>
    MicrosoftFoundry = 1,
}
