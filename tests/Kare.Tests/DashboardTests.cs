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

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/dashboard/api/snapshot")]
    public async Task ApiKeyProtectsModelAndDashboardData(string path)
    {
        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(new KareServiceOptions { ApiKey = "secret" }),
            CreateAuthentication());
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public void DashboardSessionUsesHttpOnlyCookieWithoutExposingApiKey()
    {
        var authentication = CreateAuthentication();
        var loginContext = new DefaultHttpContext();

        Assert.True(authentication.IsApiKeyValid("secret"));
        authentication.EstablishSession(loginContext);

        var setCookie = Assert.Single(loginContext.Response.Headers.SetCookie);
        Assert.NotNull(setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", setCookie, StringComparison.Ordinal);

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Headers.Cookie = setCookie.Split(';', 2)[0];
        Assert.True(authentication.IsAuthorized(requestContext));
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

    private static DashboardAuthenticationService CreateAuthentication() =>
        new(Options.Create(new KareServiceOptions { ApiKey = "secret" }));

    private sealed class CapturingRouteRecorder : IRouteRecorder
    {
        public ValueTask RecordAsync(
            RouteDecision decision,
            RouteUsage usage,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
