using System.Net;
using System.Text;
using Kare.Abstractions;
using Kare.Inference.GenieX;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class GenieXBackendTests
{
    [Fact]
    public async Task ProbeAcceptsModelListingPrecisionSuffix()
    {
        using var backend = CreateBackend(
            """{"data":[{"id":"qualcomm/qwen3_1_7b:w4a16"}],"object":"list"}""");

        var result = await backend.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal(BackendKind.GenieXQairt, backend.Kind);
    }

    [Fact]
    public async Task DisabledBackendDoesNotContactServer()
    {
        var factory = new StubHttpClientFactory(
            new HttpClient(new ThrowingHandler()));
        using var backend = new GenieXBackend(
            Options.Create(new GenieXOptions { Enabled = false }),
            factory);

        var result = await backend.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Contains("disabled", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static GenieXBackend CreateBackend(string modelsJson)
    {
        var client = new HttpClient(new StaticResponseHandler(modelsJson));
        var options = Options.Create(
            new GenieXOptions
            {
                Enabled = true,
                Endpoint = new Uri("http://127.0.0.1:18181/v1"),
                ModelId = "qualcomm/qwen3_1_7b",
            });

        return new GenieXBackend(options, new StubHttpClientFactory(client));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("http://127.0.0.1:18181/v1/models", request.RequestUri?.AbsoluteUri);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled backend contacted the server.");
    }
}
