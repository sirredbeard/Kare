using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Kare.Core;
using Kare.Core.Options;
using Kare.Service.Cache;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Kare.Service.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class HydraFusionCascadeChatClientTests
{
    [Fact]
    public async Task AuthoritativeLocalAnswerAvoidsCloudAndClearsTools()
    {
        var local = new ScriptedChatClient((_, options) =>
        {
            Assert.Null(options?.Tools);
            Assert.Equal(ChatToolMode.None, options?.ToolMode);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ANSWER:\nUse the measured runtime."));
        });
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Which runtime?")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Use the measured runtime.", response.Text);
        Assert.Equal(1, local.CallCount);
        Assert.Empty(cloud.ModelsCalled);
    }

    [Fact]
    public async Task LocalGateSelectsCloudTarget()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ESCALATE:strong")));
        var cloud = new ScriptedCloudBackend();
        var routeContext = new CascadeRouteContext();
        routeContext.Clear();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache, routeContext);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Review this design.")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cloud-strong", response.Text);
        Assert.Equal(["strong"], cloud.ModelsCalled);
        Assert.Equal("strong", routeContext.Current?.ProviderRouteId);
    }

    [Fact]
    public async Task TruncatedLocalAnswerEscalatesToCloud()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ANSWER:\nPartial answer"))
            {
                FinishReason = ChatFinishReason.Length,
            });
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Give me the complete answer.")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cloud-fast", response.Text);
        Assert.Equal(["fast"], cloud.ModelsCalled);
    }

    [Fact]
    public async Task CloudFailureBeforeResponseEscalatesToNextTarget()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ESCALATE:fast")));
        var cloud = new ScriptedCloudBackend
        {
            ResponseFailures = { ["fast"] = new CloudInferenceException("failed") },
        };
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Handle this task.")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cloud-no-tools", response.Text);
        Assert.Equal(["fast", "no-tools"], cloud.ModelsCalled);
    }

    [Fact]
    public async Task ResultJudgeRejectsDraftAndUsesOneRepairTarget()
    {
        var localCalls = 0;
        var local = new ScriptedChatClient((_, _) =>
        {
            localCalls++;
            return new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    localCalls == 1
                        ? "KARE_ESCALATE:fast"
                        : localCalls == 2
                            ? "KARE_REPAIR:incomplete"
                            : "KARE_ACCEPT"));
        });
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache, enableResultJudge: true);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Review this design.")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cloud-no-tools", response.Text);
        Assert.Equal(["fast", "no-tools"], cloud.ModelsCalled);
        Assert.Equal(3, local.CallCount);
    }

    [Fact]
    public async Task CritiqueRevisesNonStreamingCloudDraftOnce()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ESCALATE:fast")));
        var cloud = new ScriptedCloudBackend
        {
            ResponseFactory = (messages, options) =>
            {
                var instructions = messages.FirstOrDefault()?.Text ?? string.Empty;
                if (instructions.Contains("read-only critique", StringComparison.Ordinal))
                {
                    return new ChatResponse(
                        new ChatMessage(ChatRole.Assistant, "KARE_CRITIQUE:REVISE:correct the answer"));
                }

                if (instructions.Contains("Revise the draft once", StringComparison.Ordinal))
                {
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "revised answer"));
                }

                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "initial answer"));
            },
        };
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache, enableCritique: true);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Answer this request.")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("revised answer", response.Text);
        Assert.Equal(["fast", "no-tools", "fast"], cloud.ModelsCalled);
    }

    [Fact]
    public async Task CachedTargetSkipsRepeatedLocalGate()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "not a valid gate response")));
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);
        var messages = new[] { new ChatMessage(ChatRole.User, "Repeatable request.") };

        await client.GetResponseAsync(
            messages,
            cancellationToken: TestContext.Current.CancellationToken);
        await client.GetResponseAsync(
            messages,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, local.CallCount);
        Assert.Equal(["fast", "fast"], cloud.ModelsCalled);
    }

    [Fact]
    public async Task ToolContinuationReusesInitialRouteDecision()
    {
        var local = new ScriptedChatClient((_, options) =>
        {
            Assert.Equal(8, options?.MaxOutputTokens);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "fast"));
        });
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", "web_search")],
        };
        var user = new ChatMessage(ChatRole.User, "What is the weather in Seattle?");

        await client.GetResponseAsync(
            [user],
            options,
            TestContext.Current.CancellationToken);
        await client.GetResponseAsync(
            [
                user,
                new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "web_search", null)]),
                new ChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent("call-1", "partly cloudy")]),
            ],
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, local.CallCount);
        Assert.Equal(["fast", "fast"], cloud.ModelsCalled);
    }

    [Fact]
    public async Task ToolRequestUsesOnlyToolCapableCloudTargets()
    {
        var local = new ScriptedChatClient((messages, options) =>
        {
            var instructions = messages[0].Text;
            Assert.DoesNotContain("no-tools:", instructions, StringComparison.Ordinal);
            Assert.Contains("strong:", instructions, StringComparison.Ordinal);
            Assert.Contains("inspect_repository", instructions, StringComparison.Ordinal);
            Assert.True(
                options?.AdditionalProperties?.TryGetValue(
                    ContextEnrichingChatClient.SkipKnowledgeContextOptionName,
                    out var skip) == true &&
                skip is true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ANSWER:\nUnsafe local answer."));
        });
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Inspect the repository.")],
            new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(() => "ok", "inspect_repository")],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["fast"], cloud.ModelsCalled);
        Assert.NotNull(cloud.LastOptions?.Tools);
    }

    [Fact]
    public async Task ImageRequestSkipsLocalGateAndUsesImageCapableCloudTarget()
    {
        var local = new ScriptedChatClient((_, _) =>
            throw new InvalidOperationException("The local gate must not receive image requests."));
        var cloud = new ScriptedCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);
        var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new TextContent("Inspect this."), image])],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cloud-fast", response.Text);
        Assert.Equal(0, local.CallCount);
        Assert.Equal(["fast"], cloud.ModelsCalled);
    }

    [Fact]
    public async Task StreamingFailureBeforeOutputEscalatesToNextTarget()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ESCALATE:fast")));
        var cloud = new ScriptedCloudBackend
        {
            ResponseFailures = { ["fast"] = new CloudInferenceException("failed") },
        };
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);
        var output = new List<string>();

        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Stream this task.")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            output.Add(update.Text);
        }

        Assert.Equal(["fast", "no-tools"], cloud.ModelsCalled);
        Assert.Contains("cloud-no-tools", output);
    }

    [Fact]
    public async Task StreamingDoesNotSwitchTargetsAfterOutputStarts()
    {
        var local = new ScriptedChatClient((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "KARE_ESCALATE:fast")));
        var cloud = new PartialFailureCloudBackend();
        using var cache = CreateCache();
        using var client = CreateClient(local, cloud, cache);
        var output = new List<string>();

        var exception = await Assert.ThrowsAsync<CloudInferenceException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Stream this task.")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
                output.Add(update.Text);
            }
        });

        Assert.Equal("stream failed", exception.Message);
        Assert.Equal(["fast"], cloud.ModelsCalled);
        Assert.Contains("partial", output);
    }

    private static HydraFusionCascadeChatClient CreateClient(
        IChatClient local,
        ICloudInferenceBackend cloud,
        ResponseCache cache,
        CascadeRouteContext? routeContext = null,
        bool enableResultJudge = false,
        bool enableCritique = false) =>
        new(
            local,
            cloud,
            new StaticCatalog(
            [
                new CloudModelDescriptor(
                    "fast",
                    "fast-model",
                    CloudModelProvider.GitHubCopilot,
                    CloudModelTier.Fast,
                    Priority: 10,
                    SupportsTools: true,
                    SupportsImages: true),
                new CloudModelDescriptor(
                    "no-tools",
                    "no-tools-model",
                    CloudModelProvider.MicrosoftFoundry,
                    CloudModelTier.Heavy,
                    Priority: 15,
                    SupportsTools: false),
                new CloudModelDescriptor(
                    "strong",
                    "strong-model",
                    CloudModelProvider.GitHubCopilot,
                    CloudModelTier.Heavy,
                    Priority: 20,
                    SupportsTools: true,
                    SupportsImages: true),
            ]),
            new NullRouteRecorder(),
            cache,
            routeContext ?? new CascadeRouteContext(),
            Options.Create(new RoutePolicyOptions
            {
                EnableCascadeEscalation = true,
                CascadeDecisionMaxInputCharacters = 2_000,
                CascadeDecisionMaxOutputTokens = 64,
                CascadeToolDecisionMaxOutputTokens = 8,
                EnableCascadeResultJudge = enableResultJudge,
                EnableCascadeCritique = enableCritique,
            }),
            BackendKind.GenieXQairt,
            "qwen",
            NullLoggerFactory.Instance);

    private static ResponseCache CreateCache() =>
        new(Options.Create(new ResponseCacheOptions
        {
            Enabled = true,
            MaxEntries = 16,
            EntryLifetimeSeconds = 60,
        }));

    private sealed class StaticCatalog(IReadOnlyList<CloudModelDescriptor> models) : ICloudModelCatalog
    {
        public IReadOnlyList<CloudModelDescriptor> Models { get; } = models;
    }

    private sealed class NullRouteRecorder : IRouteRecorder
    {
        public ValueTask RecordAsync(
            RouteDecision decision,
            RouteUsage usage,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class ScriptedChatClient(
        Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> response) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(response(messages.ToArray(), options));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, result.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedCloudBackend : ICloudInferenceBackend
    {
        public KareRoute Route => KareRoute.CopilotLight;

        public string ModelId => "cloud";

        public List<string> ModelsCalled { get; } = [];

        public Dictionary<string, Exception> ResponseFailures { get; } = [];

        public ChatOptions? LastOptions { get; private set; }

        public Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>? ResponseFactory { get; init; }

        public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(BackendProbeResult.Available("test"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var model = options?.ModelId ?? string.Empty;
            ModelsCalled.Add(model);
            LastOptions = options;
            if (ResponseFailures.TryGetValue(model, out var failure))
            {
                throw failure;
            }

            return Task.FromResult(
                ResponseFactory?.Invoke(messages.ToArray(), options) ??
                new ChatResponse(new ChatMessage(ChatRole.Assistant, $"cloud-{model}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class PartialFailureCloudBackend : ICloudInferenceBackend
    {
        public KareRoute Route => KareRoute.CopilotLight;

        public string ModelId => "cloud";

        public List<string> ModelsCalled { get; } = [];

        public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(BackendProbeResult.Available("test"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ModelsCalled.Add(options?.ModelId ?? string.Empty);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new CloudInferenceException("stream failed");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
