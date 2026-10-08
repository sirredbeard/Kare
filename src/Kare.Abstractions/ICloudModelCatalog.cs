using Microsoft.Extensions.AI;

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
/// <param name="SupportsImages">Whether image attachments are supported.</param>
public sealed record CloudModelDescriptor(
    string Id,
    string ModelId,
    CloudModelProvider Provider,
    CloudModelTier Tier,
    int Priority,
    bool SupportsTools,
    bool SupportsImages = false,
    RouteMode AllowedModes = RouteMode.Both)
{
    /// <summary>Route recorded when this model serves a request.</summary>
    public KareRoute Route => Provider == CloudModelProvider.MicrosoftFoundry
        ? KareRoute.Foundry
        : Tier == CloudModelTier.Fast
            ? KareRoute.CopilotLight
            : KareRoute.CopilotHeavy;
}

/// <summary>Provider boundary selected for a request.</summary>
[Flags]
public enum RouteMode
{
    /// <summary>Personal repositories and accounts.</summary>
    Personal = 1,

    /// <summary>Work-owned repositories and accounts.</summary>
    Work = 2,

    /// <summary>Allow the route in either boundary.</summary>
    Both = Personal | Work,
}

/// <summary>Shared routing-mode metadata carried through a Kare request.</summary>
public static class RouteModePolicy
{
    /// <summary>Request option containing the selected provider boundary.</summary>
    public const string ModeOptionName = "kare.routing.mode";

    /// <summary>Request option containing why the boundary was selected.</summary>
    public const string ReasonOptionName = "kare.routing.reason";

    /// <summary>Returns the validated request boundary, defaulting to Personal.</summary>
    public static RouteMode GetMode(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue(ModeOptionName, out var value) == true &&
            value is string text &&
            Enum.TryParse<RouteMode>(text, ignoreCase: true, out var mode) &&
            mode is RouteMode.Personal or RouteMode.Work)
        {
            return mode;
        }

        return RouteMode.Personal;
    }

    /// <summary>Returns whether a model is allowed to receive the request.</summary>
    public static bool Allows(CloudModelDescriptor model, ChatOptions? options) =>
        (model.AllowedModes & GetMode(options)) != 0;

    /// <summary>Returns privacy-safe mode metadata for route records.</summary>
    public static string Describe(ChatOptions? options)
    {
        var mode = GetMode(options);
        var reason = options?.AdditionalProperties?.TryGetValue(ReasonOptionName, out var value) == true
            ? value?.ToString()
            : null;
        return string.IsNullOrWhiteSpace(reason)
            ? $"Routing mode: {mode}."
            : $"Routing mode: {mode} ({reason}).";
    }
}

/// <summary>Supported direct cloud providers.</summary>
public enum CloudModelProvider
{
    /// <summary>GitHub Copilot through the Copilot SDK.</summary>
    GitHubCopilot = 0,

    /// <summary>Microsoft Foundry through a configured OpenAI-compatible deployment.</summary>
    MicrosoftFoundry = 1,
}
