using System.Net;
using Kare.Service.Api;
using Kare.Service.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class NetworkAllowListMiddlewareTests
{
    [Theory]
    [InlineData("192.168.0.164", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("127.0.0.1", true)]
    public void EnforcesConfiguredNetworks(string address, bool expected)
    {
        var middleware = new NetworkAllowListMiddleware(
            _ => Task.CompletedTask,
            Options.Create(new KareServiceOptions
            {
                AllowedNetworks = { "192.168.0.0/24" },
            }));

        Assert.Equal(expected, middleware.IsAllowed(IPAddress.Parse(address)));
    }
}
