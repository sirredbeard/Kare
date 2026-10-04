using System.Net;
using Kare.Service.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Kare.Service.Api;

/// <summary>Rejects non-loopback callers outside the configured CIDR allow-list.</summary>
public sealed class NetworkAllowListMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IPNetwork[] _allowedNetworks;

    /// <summary>Creates the allow-list middleware.</summary>
    public NetworkAllowListMiddleware(
        RequestDelegate next,
        IOptions<KareServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _allowedNetworks = options.Value.AllowedNetworks
            .Select(ParseNetwork)
            .ToArray();
    }

    /// <summary>Continues only for loopback or an allowed network.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IsAllowed(remoteAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    internal bool IsAllowed(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return _allowedNetworks.Any(network => network.Contains(address));
    }

    private static IPNetwork ParseNetwork(string value)
    {
        if (!IPNetwork.TryParse(value, out var network))
        {
            throw new OptionsValidationException(
                KareServiceOptions.SectionName,
                typeof(KareServiceOptions),
                [$"Allowed network '{value}' is not a valid CIDR network."]);
        }

        return network;
    }
}
