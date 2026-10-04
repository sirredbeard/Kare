namespace Kare.Abstractions;

/// <summary>
/// Provider-neutral cloud model catalogue used by route policy before a billable call.
/// Descriptors contain no credentials or endpoint details.
/// </summary>
public interface ICloudModelCatalog
{
    /// <summary>Configured cloud models in deterministic priority order.</summary>
    IReadOnlyList<CloudModelDescriptor> Models { get; }
}

/// <summary>Configured escalation tier for a cloud model.</summary>
public enum CloudModelTier
{
    /// <summary>Low-cost, low-latency work.</summary>
    Fast = 0,

    /// <summary>Stronger single-model work.</summary>
    Heavy = 1,

    /// <summary>Primary model for complex orchestration.</summary>
    Complex = 2,

    /// <summary>Independent reviewer used by critique strategies.</summary>
    Critic = 3,
}

/// <summary>
/// Public routing metadata for one configured provider model.
/// </summary>
/// <param name="Id">Stable configuration identifier used for dispatch.</param>
/// <param name="ModelId">Concrete provider model or deployment identifier.</param>
/// <param name="Provider">Configured provider.</param>
/// <param name="Tier">Escalation tier.</param>
/// <param name="Priority">Lower values are preferred within a tier.</param>
/// <param name="SupportsTools">Whether caller-owned tool declarations are supported.</param>
public sealed record CloudModelDescriptor(
    string Id,
    string ModelId,
    CloudModelProvider Provider,
    CloudModelTier Tier,
    int Priority,
    bool SupportsTools)
{
    /// <summary>Route recorded when this model serves a request.</summary>
    public KareRoute Route => Provider == CloudModelProvider.MicrosoftFoundry
        ? KareRoute.Foundry
        : Tier == CloudModelTier.Fast
            ? KareRoute.CopilotLight
            : KareRoute.CopilotHeavy;
}

/// <summary>Supported direct cloud providers.</summary>
public enum CloudModelProvider
{
    /// <summary>GitHub Copilot through the Copilot SDK.</summary>
    GitHubCopilot = 0,

    /// <summary>Microsoft Foundry through a configured OpenAI-compatible deployment.</summary>
    MicrosoftFoundry = 1,
}
