using Kare.Service.Cache;
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

    private static ResponseCache CreateCache() =>
        new(Options.Create(new ResponseCacheOptions
        {
            Enabled = true,
            MaxEntries = 8,
            EntryLifetimeSeconds = 60,
        }));
}
