using System.Security.Cryptography;
using System.Text;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Kare.Service.Api;

/// <summary>
/// Requires a shared secret on every API request.
/// Kare holds prompts, source code, and generated patches, so an unauthenticated listener
/// is not acceptable even on a home network. The comparison is fixed time so the key
/// cannot be recovered by timing the endpoint.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expected;
    private readonly bool _enabled;
    private readonly IDashboardAuthenticationService _dashboardAuthentication;

    /// <summary>Creates the middleware.</summary>
    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptions<KareServiceOptions> options,
        IDashboardAuthenticationService dashboardAuthentication)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dashboardAuthentication);

        _next = next;
        _expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);
        _enabled = _expected.Length > 0;
        _dashboardAuthentication = dashboardAuthentication;
    }

    /// <summary>Validates the bearer token, then continues the pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var dashboardApi = context.Request.Path.StartsWithSegments("/dashboard/api");
        var dashboardLogin =
            context.Request.Path.Equals("/dashboard/api/session") &&
            HttpMethods.IsPost(context.Request.Method);
        var protectedPath = context.Request.Path.StartsWithSegments("/v1") || dashboardApi;

        if (!_enabled || !protectedPath || dashboardLogin)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var authorized = dashboardApi
            ? _dashboardAuthentication.IsAuthorized(context) || IsBearerAuthorized(context)
            : IsBearerAuthorized(context);

        if (!authorized)
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
