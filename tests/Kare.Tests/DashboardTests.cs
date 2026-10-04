using Kare.Abstractions;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Service.Api;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace Kare.Tests;

public sealed class DashboardTests
{
    [Fact]
    public async Task ActivityRecordsRouteWithoutInventingUnknownUsage()
    {
        var collector = new InMemoryMetricsCollector();
        var limits = new InferenceLimits { MaxConcurrentInference = 1 };
        using var gate = new InferenceGate(Options.Create(limits));
        var activity = new DashboardActivity(
            new CapturingRouteRecorder(),
            collector,
            gate,
            Options.Create(limits));

        await activity.RecordAsync(
            new RouteDecision(
                KareRoute.LocalSlm,
                "test route",
                "test-model",
                BackendKind.OnnxGenAiCpu,
                IsBillable: false),
            new RouteUsage(
                TimeSpan.FromMilliseconds(120),
                TimeSpan.FromMilliseconds(300),
                InputTokens: null,
                OutputTokens: null,
                Succeeded: true),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(collector.GetRequests());
        Assert.Null(request.OutputTokens);
        Assert.Null(request.DecodeTokensPerSecond);
        Assert.Equal("LocalSlm", request.Route);

        var workload = Assert.IsType<DashboardMetrics.WorkloadSnapshot>(collector.GetWorkload());
        Assert.Equal(1, workload.TotalRequestsProcessed);
        Assert.Equal(120, workload.AverageTimeToFirstTokenMs);
    }

    [Fact]
    public void RegistryEntriesAreUpsertedByStableIdentity()
    {
        var collector = new InMemoryMetricsCollector();
        var created = DateTime.UtcNow.AddMinutes(-1);

        collector.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            "key",
            created,
            LastAccessedAt: null,
            SizeBytes: 10,
            "application/json"));
        collector.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            "key",
            DateTime.UtcNow,
            DateTime.UtcNow,
            SizeBytes: 20,
            "application/json"));

        var entry = Assert.Single(collector.GetCacheEntries());
        Assert.Equal(created, entry.CreatedAt);
        Assert.Equal(20, entry.SizeBytes);
        Assert.NotNull(entry.LastAccessedAt);
    }

    [Fact]
    public async Task ApiKeyProtectsModelDataEvenForLanCaller()
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new KareServiceOptions { ApiKey = "secret" }));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.0.25");
        context.Request.Path = "/v1/models";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task DashboardDataDoesNotRequireApiKeyAfterNetworkAllowList()
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new KareServiceOptions { ApiKey = "secret" }));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.0.25");
        context.Request.Path = "/dashboard/api/snapshot";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public void ModelUsageAggregatesTokenAndLatencyMetrics()
    {
        var collector = new InMemoryMetricsCollector();
        collector.RecordRequest(CreateRequest(inputTokens: 10, outputTokens: 5, succeeded: true));
        collector.RecordRequest(CreateRequest(inputTokens: 20, outputTokens: 7, succeeded: false));

        var usage = Assert.Single(collector.GetModelUsage());
        Assert.Equal(2, usage.RequestCount);
        Assert.Equal(1, usage.SuccessfulRequests);
        Assert.Equal(1, usage.FailedRequests);
        Assert.Equal(30, usage.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Equal(120, usage.AverageTimeToFirstTokenMs);
        Assert.Equal(300, usage.AverageTotalDurationMs);
    }

    [Fact]
    public async Task EnabledSkillIsInjectedIntoLocalContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var skillPath = Path.Combine(directory, "SKILL.md");
        var statePath = Path.Combine(directory, "registry.json");
        await File.WriteAllTextAsync(
            skillPath,
            "Always use the measured device runtime.",
            TestContext.Current.CancellationToken);

        try
        {
            var service = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await service.AddSkillAsync(
                new CreateDashboardSkillRequest(
                    "device-runtime",
                    skillPath,
                    "Device runtime rule",
                    Enabled: true),
                TestContext.Current.CancellationToken);

            var enriched = await service.AddLocalContextAsync(
                [new ChatMessage(ChatRole.User, "Which runtime should I use?")],
                TestContext.Current.CancellationToken);

            Assert.Equal(2, enriched.Count);
            Assert.Contains(
                "Always use the measured device runtime.",
                enriched[0].Contents.OfType<TextContent>().Single().Text,
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(skillPath))
            {
                File.Delete(skillPath);
            }

            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task EnabledHttpsSkillIsFetchedAndInjectedIntoLocalContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new StaticResponseHandler(
            "Use the remote measured runtime.",
            "text/markdown"));

        try
        {
            var service = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            var skill = await service.AddSkillAsync(
                new CreateDashboardSkillRequest(
                    "remote-runtime",
                    "https://1.1.1.1/SKILL.md",
                    "Remote runtime rule",
                    Enabled: true),
                TestContext.Current.CancellationToken);

            Assert.Equal("ready", skill.Status);
            var enriched = await service.AddLocalContextAsync(
                [new ChatMessage(ChatRole.User, "Which runtime should I use?")],
                TestContext.Current.CancellationToken);

            Assert.Equal(2, enriched.Count);
            Assert.Contains(
                "Use the remote measured runtime.",
                enriched[0].Contents.OfType<TextContent>().Single().Text,
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task ConnectedMcpCapabilitiesAreInjectedAsAdvisoryContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new StaticResponseHandler(
            """{"jsonrpc":"2.0","id":1,"result":{"capabilities":{"tools":{},"resources":{}}}}""",
            "application/json"));

        try
        {
            var collector = new InMemoryMetricsCollector();
            var service = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            var initialVersion = service.ContextVersion;

            await service.AddMcpServerAsync(
                new CreateDashboardMcpServerRequest("workspace-tools", "https://1.1.1.1/mcp"),
                TestContext.Current.CancellationToken);
            var enriched = await service.AddLocalContextAsync(
                [new ChatMessage(ChatRole.User, "What tools are connected?")],
                TestContext.Current.CancellationToken);

            Assert.NotEqual(initialVersion, service.ContextVersion);
            var context = enriched[0].Contents.OfType<TextContent>().Single().Text;
            Assert.Contains("Connected MCP server: workspace-tools", context, StringComparison.Ordinal);
            Assert.Contains("Capabilities: tools, resources", context, StringComparison.Ordinal);
            Assert.Contains("do not claim to have called a server", context, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task LocalContextMergesWithExistingLeadingSystemMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var skillPath = Path.Combine(directory, "SKILL.md");
        var statePath = Path.Combine(directory, "registry.json");
        await File.WriteAllTextAsync(
            skillPath,
            "Use authoritative context.",
            TestContext.Current.CancellationToken);

        try
        {
            var service = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await service.AddSkillAsync(
                new CreateDashboardSkillRequest("context", skillPath, "test", Enabled: true),
                TestContext.Current.CancellationToken);

            var enriched = await service.AddLocalContextAsync(
                [
                    new ChatMessage(ChatRole.System, "Cascade instructions."),
                    new ChatMessage(ChatRole.User, "Route this."),
                ],
                TestContext.Current.CancellationToken);
            var systemText = string.Join(
                "\n",
                enriched[0].Contents.OfType<TextContent>().Select(static content => content.Text));

            Assert.Equal(2, enriched.Count);
            Assert.Equal(ChatRole.System, enriched[0].Role);
            Assert.Contains("Use authoritative context.", systemText, StringComparison.Ordinal);
            Assert.Contains("Cascade instructions.", systemText, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(skillPath))
            {
                File.Delete(skillPath);
            }

            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory);
        }
    }

    private static DashboardMetrics.RequestMetric CreateRequest(
        long inputTokens,
        long outputTokens,
        bool succeeded) =>
        new(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            "LocalSlm",
            "test-model",
            ProviderRouteId: null,
            Backend: "GenieXQairt",
            IsBillable: false,
            IsFallback: false,
            Succeeded: succeeded,
            TimeToFirstTokenMs: 120,
            TotalDurationMs: 300,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            DecodeTokensPerSecond: 4);

    private sealed class CapturingRouteRecorder : IRouteRecorder
    {
        public ValueTask RecordAsync(
            RouteDecision decision,
            RouteUsage usage,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StaticHttpClientFactory(HttpClient? client = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client ?? new();
    }

    private sealed class StaticResponseHandler(string content, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType),
            });
    }
}
