using System.Security.Cryptography;
using System.Text;
using Kare.Service.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Kare.Service.Api;

/// <summary>
/// Requires a shared secret on OpenAI-compatible API requests.
/// Kare holds prompts, source code, and generated patches, so an unauthenticated listener
/// is not acceptable even on a home network. Dashboard routes rely on the network
/// allow-list instead. The comparison is fixed time so the key cannot be recovered by
/// timing the endpoint.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expected;
    private readonly bool _enabled;

    /// <summary>Creates the middleware.</summary>
    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptions<KareServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);
        _enabled = _expected.Length > 0;
    }

    /// <summary>Validates the bearer token, then continues the pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var dashboardApi = context.Request.Path.StartsWithSegments("/dashboard/api");
        if (dashboardApi || !_enabled || !context.Request.Path.StartsWithSegments("/v1"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!IsBearerAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool IsBearerAuthorized(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }
}
