using Kare.Abstractions;
using Kare.Core.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kare.Tests;

public sealed class RoutingChatClientTests
{
    [Fact]
    public async Task AppliesSelectedProviderModelBeforeCloudDispatch()
    {
        var cloud = new CapturingCloudBackend();
        var decision = new RouteDecision(
            KareRoute.CopilotLight,
            "test",
            "mai-code-1.1-flash",
            BackendKind.Remote,
            IsBillable: true,
            ProviderRouteId: "copilot-flash");
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var client = new KareRoutingChatClient(
            new StaticChatClient("local"),
            cloud,
            new StaticRouteSelector(decision),
            new NullRouteRecorder(),
            loggerFactory);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "kare-fast" },
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-flash", cloud.SelectedModelId);
    }

    private sealed class StaticRouteSelector(RouteDecision decision) : IRouteSelector
    {
        public ValueTask<RouteDecision> SelectAsync(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(decision);
    }

    private sealed class CapturingCloudBackend : ICloudInferenceBackend
    {
        public KareRoute Route => KareRoute.CopilotLight;
        public string ModelId => "unused";
        public string? SelectedModelId { get; private set; }

        public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(BackendProbeResult.Available("test"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SelectedModelId = options?.ModelId;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "cloud")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SelectedModelId = options?.ModelId;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "cloud");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StaticChatClient(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, response);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NullRouteRecorder : IRouteRecorder
    {
        public ValueTask RecordAsync(
            RouteDecision decision,
            RouteUsage usage,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
