namespace Kare.Abstractions;

/// <summary>
/// Where a request was actually served from. Ordered cheapest first.
/// Every served request records exactly one of these.
/// </summary>
public enum KareRoute
{
    /// <summary>No route selected yet.</summary>
    None = 0,

    /// <summary>A validated local cache entry answered the request.</summary>
    Cache = 1,

    /// <summary>The local small language model on the device answered the request.</summary>
    LocalSlm = 2,

    /// <summary>A low cost GitHub Copilot model answered the request.</summary>
    CopilotLight = 3,

    /// <summary>A high cost GitHub Copilot model answered the request.</summary>
    CopilotHeavy = 4,

    /// <summary>A Microsoft Foundry deployment answered the request.</summary>
    Foundry = 5,
}
