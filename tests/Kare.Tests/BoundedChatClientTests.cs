using Kare.Core;
using Kare.Core.Inference;
using Kare.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class BoundedChatClientTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static BoundedChatClient Create(IChatClient inner, InferenceLimits limits) =>
        new(inner, new InferenceGate(Options.Create(limits)), Options.Create(limits));

    [Fact]
    public async Task RejectsPromptOverCharacterLimitWithoutCallingTheModel()
    {
        var inner = new FakeChatClient();
        var client = Create(inner, new InferenceLimits { MaxPromptCharacters = 1024 });
        var messages = new[] { new ChatMessage(ChatRole.User, new string('x', 2048)) };

        var error = await Assert.ThrowsAsync<PromptTooLargeException>(
            () => client.GetResponseAsync(messages, cancellationToken: TestToken));

        Assert.Equal("characters", error.Unit);
        Assert.Equal(1024, error.Limit);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task ClampsOutputTokensToTheConfiguredCeiling()
    {
        var inner = new FakeChatClient();
        var client = Create(inner, new InferenceLimits { MaxOutputTokens = 256 });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { MaxOutputTokens = 99_999 },
            TestToken);

        Assert.Equal(256, inner.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task AppliesTheCeilingWhenTheCallerSuppliesNoOptions()
    {
        var inner = new FakeChatClient();
        var client = Create(inner, new InferenceLimits { MaxOutputTokens = 64 });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestToken);

        Assert.Equal(64, inner.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task DoesNotMutateCallerSuppliedOptions()
    {
        var inner = new FakeChatClient();
        var client = Create(inner, new InferenceLimits { MaxOutputTokens = 32 });
        var callerOptions = new ChatOptions { MaxOutputTokens = 4096 };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            callerOptions,
            TestToken);

        Assert.Equal(4096, callerOptions.MaxOutputTokens);
    }

    [Fact]
    public async Task LeavesAnOutputRequestBelowTheCeilingAlone()
    {
        var inner = new FakeChatClient();
        var client = Create(inner, new InferenceLimits { MaxOutputTokens = 512 });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { MaxOutputTokens = 100 },
            TestToken);

        Assert.Equal(100, inner.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task CancelsGenerationThatExceedsTheConfiguredBudget()
    {
        var limits = new InferenceLimits { GenerationTimeoutSeconds = 1 };
        var inner = new FakeChatClient(token => Task.Delay(TimeSpan.FromSeconds(30), token));
        var client = Create(inner, limits);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestToken));
    }
}
