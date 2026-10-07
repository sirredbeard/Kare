namespace Kare.Abstractions;

/// <summary>
/// The recorded outcome of route selection. Kare requires a reason for every
/// decision so a fallback is never silent and a billable call is never implicit.
/// </summary>
/// <param name="Route">The route that will serve, or did serve, the request.</param>
/// <param name="Reason">Why this route was selected. Must not contain prompt text or secrets.</param>
/// <param name="ModelId">The model identifier the route will use.</param>
/// <param name="Backend">The local execution path, or <see cref="BackendKind.Remote"/>.</param>
/// <param name="IsBillable">True when the route spends Copilot AI credits or Azure credit.</param>
/// <param name="FellBackFrom">Set when this route replaced a previously selected route.</param>
/// <param name="ProviderRouteId">Stable provider-neutral catalogue entry used for dispatch.</param>
public readonly record struct RouteDecision(
    KareRoute Route,
    string Reason,
    string ModelId,
    BackendKind Backend,
    bool IsBillable,
    KareRoute FellBackFrom = KareRoute.None,
    string? ProviderRouteId = null)
{
    /// <summary>True when this decision replaced an earlier route.</summary>
    public bool IsFallback => FellBackFrom != KareRoute.None;
}
