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
        Assert.Equal(2, usage.PricedRequests);
        Assert.Equal(30, usage.PricedInputTokens);
        Assert.Equal(12, usage.PricedOutputTokens);
        Assert.Equal(1, usage.SuccessfulPricedRequests);
        Assert.Equal(10, usage.SuccessfulPricedInputTokens);
        Assert.Equal(5, usage.SuccessfulPricedOutputTokens);
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

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalContextLoadsOnlyRelevantSkillBodies()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var relevantPath = Path.Combine(directory, "runtime.md");
        var unrelatedPath = Path.Combine(directory, "database.md");
        var statePath = Path.Combine(directory, "registry.json");
        await File.WriteAllTextAsync(
            relevantPath,
            "Use the measured GenieX runtime on the device.",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            unrelatedPath,
            "Use PostgreSQL migrations for persistence.",
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
                    relevantPath,
                    "GenieX device runtime",
                    Enabled: true),
                TestContext.Current.CancellationToken);
            await service.AddSkillAsync(
                new CreateDashboardSkillRequest(
                    "database",
                    unrelatedPath,
                    "PostgreSQL persistence",
                    Enabled: true),
                TestContext.Current.CancellationToken);

            var enriched = await service.AddLocalContextAsync(
                [new ChatMessage(ChatRole.User, "Which GenieX runtime should I use?")],
                TestContext.Current.CancellationToken);
            var context = enriched[0].Contents.OfType<TextContent>().Single().Text;

            Assert.Contains("Use the measured GenieX runtime", context, StringComparison.Ordinal);
            Assert.DoesNotContain("Use PostgreSQL migrations", context, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LocalContextSelectsGoSkillAndExcludesUnrelatedDotnetSkillForGoRequest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var goPath = Path.Combine(directory, "go-errors.md");
        var dotnetPath = Path.Combine(directory, "dotnet-di.md");
        var statePath = Path.Combine(directory, "registry.json");
        await File.WriteAllTextAsync(
            goPath,
            "Wrap Go errors with fmt.Errorf and the %w verb for unwrapping.",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            dotnetPath,
            "Register dotnet services with AddSingleton for dependency injection.",
            TestContext.Current.CancellationToken);

        try
        {
            var collector = new InMemoryMetricsCollector();
            var service = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await service.AddSkillAsync(
                new CreateDashboardSkillRequest("go-errors", goPath, "Go error wrapping", Enabled: true),
                TestContext.Current.CancellationToken);
            await service.AddSkillAsync(
                new CreateDashboardSkillRequest("dotnet-di", dotnetPath, "dotnet dependency injection", Enabled: true),
                TestContext.Current.CancellationToken);

            var enriched = await service.AddLocalContextAsync(
                [new ChatMessage(ChatRole.User, "How do I wrap a Go error for unwrapping?")],
                TestContext.Current.CancellationToken);
            var context = enriched[0].Contents.OfType<TextContent>().Single().Text;

            Assert.Contains("Wrap Go errors with fmt.Errorf", context, StringComparison.Ordinal);
            Assert.DoesNotContain("AddSingleton for dependency injection", context, StringComparison.Ordinal);

            var goSkill = Assert.Single(collector.GetSkills(), item => item.Name == "go-errors");
            var dotnetSkill = Assert.Single(collector.GetSkills(), item => item.Name == "dotnet-di");
            Assert.Contains("go", goSkill.Tags!);
            Assert.Contains("dotnet", dotnetSkill.Tags!);
            Assert.DoesNotContain("dotnet", goSkill.Tags!);
            Assert.DoesNotContain("go", dotnetSkill.Tags!);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
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

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteSkillAndContextVersionSurviveRestart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new StaticResponseHandler(
            "Use the restart-safe measured runtime.",
            "text/markdown"));

        try
        {
            var first = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await first.AddSkillAsync(
                new CreateDashboardSkillRequest(
                    "remote-runtime",
                    "https://1.1.1.1/SKILL.md",
                    "Remote runtime rule",
                    Enabled: true),
                TestContext.Current.CancellationToken);
            var persistedVersion = first.ContextVersion;

            var collector = new InMemoryMetricsCollector();
            var second = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(new HttpClient(new FailingResponseHandler())),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await second.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => collector.GetSkills().Any(item => item.Name == "remote-runtime"),
                    TestContext.Current.CancellationToken);
                var enriched = await second.AddLocalContextAsync(
                    [new ChatMessage(ChatRole.User, "Which runtime should I use?")],
                    TestContext.Current.CancellationToken);

                Assert.Equal(persistedVersion, second.ContextVersion);
                Assert.Contains(
                    "Use the restart-safe measured runtime.",
                    enriched[0].Contents.OfType<TextContent>().Single().Text,
                    StringComparison.Ordinal);
                Assert.True(File.Exists(statePath + ".bak1"));
            }
            finally
            {
                await second.StopAsync(TestContext.Current.CancellationToken);
                second.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthoritativeSourceContentSurvivesRestartAndFailedRefresh()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new StaticResponseHandler(
            "The measured restart path uses GenieX.",
            "text/plain"));

        try
        {
            var first = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await first.AddSourceAsync(
                new CreateAuthoritativeSourceRequest("https://1.1.1.1/reference"),
                TestContext.Current.CancellationToken);

            var collector = new InMemoryMetricsCollector();
            var second = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(new HttpClient(new FailingResponseHandler())),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await second.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => collector.GetRoutingDecisionUrls().Count > 0,
                    TestContext.Current.CancellationToken);
                var enriched = await second.AddLocalContextAsync(
                    [new ChatMessage(ChatRole.User, "Which restart path is measured?")],
                    TestContext.Current.CancellationToken);

                Assert.Contains(
                    "The measured restart path uses GenieX.",
                    enriched[0].Contents.OfType<TextContent>().Single().Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                await second.StopAsync(TestContext.Current.CancellationToken);
                second.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LastKnownMcpCapabilitiesSurviveFailedStartupProbe()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new StaticResponseHandler(
            """{"jsonrpc":"2.0","id":1,"result":{"capabilities":{"tools":{},"resources":{}}}}""",
            "application/json"));

        try
        {
            var first = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await first.AddMcpServerAsync(
                new CreateDashboardMcpServerRequest("workspace-tools", "https://1.1.1.1/mcp"),
                TestContext.Current.CancellationToken);

            var collector = new InMemoryMetricsCollector();
            var second = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(new HttpClient(new FailingResponseHandler())),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);
            await second.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => collector.GetMcpServers()
                        .Any(item => item.Name == "workspace-tools" && item.LastCheckedAt is not null),
                    TestContext.Current.CancellationToken);
                var metric = Assert.Single(collector.GetMcpServers());

                Assert.False(metric.Connected);
                Assert.Equal(["tools", "resources"], metric.Capabilities);
                Assert.NotNull(metric.LastConnectedAt);
            }
            finally
            {
                await second.StopAsync(TestContext.Current.CancellationToken);
                second.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task McpToolsAreProbedAndSelectedForMatchingRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new McpToolResponseHandler());

        try
        {
            var collector = new InMemoryMetricsCollector();
            var service = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);

            await service.AddMcpServerAsync(
                new CreateDashboardMcpServerRequest("workspace-tools", "https://1.1.1.1/mcp"),
                TestContext.Current.CancellationToken);
            var enriched = await service.AddLocalContextAsync(
                [new ChatMessage(ChatRole.User, "Inspect the repository files.")],
                TestContext.Current.CancellationToken);

            var server = Assert.Single(collector.GetMcpServers());
            var tool = Assert.Single(server.Tools!, item => item.Name == "inspect_repository");
            Assert.Equal("inspect_repository", tool.Name);
            Assert.Equal(1, tool.Description.Length);
            Assert.Contains("inspect_repository", enriched[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task McpServerKeywordsAreDerivedFromToolNamesAndDescriptions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new KeywordMcpToolResponseHandler());

        try
        {
            var collector = new InMemoryMetricsCollector();
            var service = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);

            await service.AddMcpServerAsync(
                new CreateDashboardMcpServerRequest("deploy-tools", "https://1.1.1.1/mcp"),
                TestContext.Current.CancellationToken);

            var server = Assert.Single(collector.GetMcpServers());
            Assert.Contains("deploy", server.Keywords!);
            Assert.Contains("docker", server.Keywords!);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory, recursive: true);
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

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthoritativeSourceTimeoutIsRecordedWithoutStoppingService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new DelayedResponseHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(10),
        };

        try
        {
            var service = new DashboardKnowledgeService(
                new InMemoryMetricsCollector(),
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);

            var source = await service.AddSourceAsync(
                new CreateAuthoritativeSourceRequest("https://1.1.1.1/*"),
                TestContext.Current.CancellationToken);

            Assert.Equal("failed", source.Status);
            Assert.NotNull(source.Error);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CrawledSourceRecordsTopicsAndHeadingsForTableOfContentsRecognition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "registry.json");
        using var client = new HttpClient(new StaticResponseHandler(
            """
            # Deploying with Docker

            Use Go and Docker for the deployment pipeline.
            """,
            "text/markdown"));

        try
        {
            var collector = new InMemoryMetricsCollector();
            var service = new DashboardKnowledgeService(
                collector,
                new StaticHttpClientFactory(client),
                NullLogger<DashboardKnowledgeService>.Instance,
                statePath);

            await service.AddSourceAsync(
                new CreateAuthoritativeSourceRequest("https://1.1.1.1/reference"),
                TestContext.Current.CancellationToken);

            var route = Assert.Single(collector.GetRoutingDecisionUrls());
            Assert.Contains("go", route.Topics!);
            Assert.Contains("docker", route.Topics!);
            Assert.Contains("deploy", route.Topics!);
            Assert.Contains("Deploying with Docker", route.Headings!);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            Directory.Delete(directory, recursive: true);
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

    private sealed class McpToolResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            var content = body.Contains("tools/list", StringComparison.Ordinal)
                ? """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"inspect_repository","description":"x","inputSchema":{"type":"object","properties":{"path":{"type":"string"}}}},{"name":"mutate_repository","description":"y","inputSchema":{"type":"object"}}]}}"""
                : """{"jsonrpc":"2.0","id":1,"result":{"capabilities":{"tools":{}}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class KeywordMcpToolResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            var content = body.Contains("tools/list", StringComparison.Ordinal)
                ? """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"deploy_image","description":"Deploy the docker image to production","inputSchema":{"type":"object"}}]}}"""
                : """{"jsonrpc":"2.0","id":1,"result":{"capabilities":{"tools":{}}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class DelayedResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("late", Encoding.UTF8, "text/plain"),
            };
        }
    }

    private sealed class FailingResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected dashboard state was not loaded.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }
}
