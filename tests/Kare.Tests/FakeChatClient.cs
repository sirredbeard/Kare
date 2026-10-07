using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Kare.Tests;

/// <summary>
/// A chat client that records what it was called with and optionally blocks, so the
/// bounded gateway can be tested without loading a model.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<CancellationToken, Task>? _onCall;

    public FakeChatClient(Func<CancellationToken, Task>? onCall = null) => _onCall = onCall;

    public ChatOptions? LastOptions { get; private set; }

    public int CallCount { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        CallCount++;

        if (_onCall is not null)
        {
            await _onCall(cancellationToken);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        CallCount++;

        if (_onCall is not null)
        {
            await _onCall(cancellationToken);
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
