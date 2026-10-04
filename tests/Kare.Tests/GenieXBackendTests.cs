using System.Net;
using System.Text;
using Kare.Abstractions;
using Kare.Inference.GenieX;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class GenieXBackendTests
{
    [Fact]
    public async Task ProbeAcceptsModelListingPrecisionSuffix()
    {
        var handler = new GenieXHandler();
        using var backend = CreateBackend(handler);

        var result = await backend.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal(BackendKind.GenieXQairt, backend.Kind);
        Assert.Equal(1, handler.CompletionRequests);
    }

    [Fact]
    public async Task ProbeRejectsModelThatCannotRunInference()
    {
        using var backend = CreateBackend(
            new GenieXHandler(HttpStatusCode.InternalServerError));

        var result = await backend.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Contains("500", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestFailureReportsBackendUnavailable()
    {
        using var backend = CreateBackend(
            new GenieXHandler(HttpStatusCode.InternalServerError));

        var error = await Assert.ThrowsAsync<LocalInferenceException>(
            () => backend.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("GenieX request failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CascadeGateDisablesThinkingWithoutSdkRetries()
    {
        var handler = new GenieXHandler();
        using var backend = CreateBackend(handler);

        var response = await backend.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "route this")],
            new ChatOptions
            {
                MaxOutputTokens = 64,
                AdditionalProperties = new()
                {
                    [GenieXBackend.DisableThinkingOptionName] = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        Assert.Equal(1, handler.CompletionRequests);
        Assert.False(handler.LastEnableThink);
    }

    [Fact]
    public async Task StreamingFailureReportsBackendUnavailable()
    {
        using var backend = CreateBackend(
            new GenieXHandler(HttpStatusCode.InternalServerError));

        var error = await Assert.ThrowsAsync<LocalInferenceException>(async () =>
        {
            await foreach (var _ in backend.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Contains("GenieX request failed", error.Message, StringComparison.Ordinal);
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

    private static GenieXBackend CreateBackend(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
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

    private sealed class GenieXHandler(
        HttpStatusCode completionStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int CompletionRequests { get; private set; }

        public bool? LastEnableThink { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("http://127.0.0.1:18181/v1/models", request.RequestUri?.AbsoluteUri);
                return Task.FromResult(
                    JsonResponse(
                        """{"data":[{"id":"qualcomm/qwen3_1_7b:w4a16"}],"object":"list"}"""));
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "http://127.0.0.1:18181/v1/chat/completions",
                request.RequestUri?.AbsoluteUri);
            CompletionRequests++;
            var requestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (requestBody?.Contains("\"enable_think\":false", StringComparison.Ordinal) == true)
            {
                LastEnableThink = false;
            }

            if (completionStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(completionStatus));
            }

            return Task.FromResult(
                JsonResponse(
                    """
                    {
                      "id": "chatcmpl-readiness",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "qualcomm/qwen3_1_7b",
                      "choices": [
                        {
                          "index": 0,
                          "message": { "role": "assistant", "content": "ready" },
                          "finish_reason": "stop"
                        }
                      ],
                      "usage": {
                        "prompt_tokens": 1,
                        "completion_tokens": 1,
                        "total_tokens": 2
                      }
                    }
                    """));
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The disabled backend contacted the server.");
    }
}
