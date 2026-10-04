using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kare.Service.Options;
using Microsoft.Extensions.Options;

namespace Kare.Service.Dashboard;

/// <summary>Authenticates dashboard browser sessions without exposing the service API key.</summary>
public interface IDashboardAuthenticationService
{
    bool IsAuthorized(HttpContext context);
    bool IsApiKeyValid(string apiKey);
    bool CanAutomaticallyAuthorize(HttpContext context);
    void EstablishSession(HttpContext context);
    void ClearSession(HttpContext context);
}

/// <summary>Process-local dashboard session authentication.</summary>
public sealed class DashboardAuthenticationService : IDashboardAuthenticationService
{
    private const string CookieName = "KareDashboardSession";
    private readonly byte[] _apiKey;
    private readonly byte[] _sessionToken = RandomNumberGenerator.GetBytes(32);

    public DashboardAuthenticationService(IOptions<KareServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _apiKey = Encoding.UTF8.GetBytes(options.Value.ApiKey);
    }

    public bool IsAuthorized(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Cookies.TryGetValue(CookieName, out var value) &&
            TryDecode(value, out var presented) &&
            CryptographicOperations.FixedTimeEquals(presented, _sessionToken);
    }

    public bool IsApiKeyValid(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        var presented = Encoding.UTF8.GetBytes(apiKey);
        return _apiKey.Length > 0 &&
            CryptographicOperations.FixedTimeEquals(presented, _apiKey);
    }

    public bool CanAutomaticallyAuthorize(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address);
    }

    public void EstablishSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Append(
            CookieName,
            Convert.ToBase64String(_sessionToken),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                Path = "/dashboard",
            });
    }

    public void ClearSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Delete(
            CookieName,
            new CookieOptions { Path = "/dashboard" });
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

/// <summary>Dashboard login payload.</summary>
public sealed record DashboardLoginRequest(string ApiKey);
