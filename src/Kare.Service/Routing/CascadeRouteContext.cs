using System.Threading;
using Kare.Abstractions;

namespace Kare.Service.Routing;

/// <summary>Request-flow route selected by the HydraFusion cascade.</summary>
public sealed class CascadeRouteContext
{
    private readonly AsyncLocal<RouteState?> _current = new();

    public RouteDecision? Current
    {
        get => _current.Value?.Decision;
        set
        {
            var state = _current.Value;
            if (state is null)
            {
                state = new RouteState();
                _current.Value = state;
            }

            state.Decision = value;
        }
    }

    /// <summary>Starts an isolated request scope whose mutations survive awaited child calls.</summary>
    public void Clear() => _current.Value = new RouteState();

    private sealed class RouteState
    {
        public RouteDecision? Decision { get; set; }
    }
}
