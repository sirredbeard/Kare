using Kare.Abstractions;
using Kare.Service.Cache;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class ResponseCacheTests
{
    [Fact]
    public void DeterministicTextResponseRoundTrips()
    {
        using var cache = CreateCache();
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var options = new ChatOptions { ModelId = "kare-local", Temperature = 0 };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "cached"));

        cache.Set(messages, options, streaming: false, response);

        Assert.True(cache.TryGet(messages, options, streaming: false, out var cached));
        Assert.Same(response, cached);
    }

    [Fact]
    public void ToolBearingRequestIsNotCached()
    {
        using var cache = CreateCache();
        var messages = new[] { new ChatMessage(ChatRole.User, "use a tool") };
        var options = new ChatOptions
        {
            ModelId = "kare-local",
            Tools = [AIFunctionFactory.Create(() => "result", "test_tool")],
        };

        cache.Set(
            messages,
            options,
            streaming: false,
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "not cached")));

        Assert.False(cache.TryGet(messages, options, streaming: false, out _));
    }

    [Fact]
    public void DashboardCanInspectAndRemoveCacheMetadata()
    {
        var dashboard = new InMemoryMetricsCollector();
        using var cache = CreateCache(dashboard);
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var options = new ChatOptions { ModelId = "kare-local", Temperature = 0 };

        cache.Set(
            messages,
            options,
            streaming: false,
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "cached")));

        var entry = Assert.Single(dashboard.GetCacheEntries());
        cache.Remove(entry.Key);

        Assert.Empty(dashboard.GetCacheEntries());
        Assert.False(cache.TryGet(messages, options, streaming: false, out _));
    }

    [Fact]
    public void ClearRemovesEveryTrackedResponse()
    {
        var dashboard = new InMemoryMetricsCollector();
        using var cache = CreateCache(dashboard);
        var options = new ChatOptions { ModelId = "kare-local", Temperature = 0 };

        cache.Set(
            [new ChatMessage(ChatRole.User, "first")],
            options,
            streaming: false,
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "one")));
        cache.Set(
            [new ChatMessage(ChatRole.User, "second")],
            options,
            streaming: false,
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "two")));

        Assert.Equal(2, dashboard.GetCacheEntries().Count);
        cache.Clear();

        Assert.Empty(dashboard.GetCacheEntries());
    }

    [Fact]
    public void CascadeTargetRoundTripsForToolRequest()
    {
        using var cache = CreateCache();
        var messages = new[] { new ChatMessage(ChatRole.User, "route this") };
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "result", "test_tool")],
        };
        var candidates = CreateCandidates();

        cache.SetCascadeTarget(messages, options, candidates, "fast");

        Assert.True(cache.TryGetCascadeTarget(messages, options, candidates, out var target));
        Assert.Equal("fast", target);
    }

    [Fact]
    public void KnowledgeVersionInvalidatesCascadeTarget()
    {
        var knowledge = new MutableKnowledgeService();
        using var cache = CreateCache(knowledge: knowledge);
        var messages = new[] { new ChatMessage(ChatRole.User, "route this") };
        var candidates = CreateCandidates();

        cache.SetCascadeTarget(messages, options: null, candidates, "fast");
        knowledge.ContextVersion = "2";

        Assert.False(cache.TryGetCascadeTarget(messages, options: null, candidates, out _));
    }

    private static ResponseCache CreateCache(
        IDashboardMetricsCollector? dashboard = null,
        IDashboardKnowledgeService? knowledge = null) =>
        new(Options.Create(new ResponseCacheOptions
        {
            Enabled = true,
            MaxEntries = 8,
            EntryLifetimeSeconds = 60,
        }), dashboard, knowledge);

    private static CloudModelDescriptor[] CreateCandidates() =>
    [
        new(
            "fast",
            "fast-model",
            CloudModelProvider.GitHubCopilot,
            CloudModelTier.Fast,
            Priority: 10,
            SupportsTools: true),
    ];

    private sealed class MutableKnowledgeService : IDashboardKnowledgeService
    {
        public string ContextVersion { get; set; } = "1";

        public Task<DashboardMetrics.RoutingDecisionUrl> AddSourceAsync(
            CreateAuthoritativeSourceRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RemoveSourceAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DashboardMetrics.SkillInfo> AddSkillAsync(
            CreateDashboardSkillRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RemoveSkillAsync(string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DashboardMetrics.McpServerInfo> AddMcpServerAsync(
            CreateDashboardMcpServerRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RemoveMcpServerAsync(string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChatMessage>> AddLocalContextAsync(
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
