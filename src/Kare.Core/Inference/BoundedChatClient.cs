using System.Runtime.CompilerServices;
using Kare.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Kare.Core.Inference;

/// <summary>
/// Applies Kare's input, output, concurrency, and time bounds to an inner chat client.
/// Every bound is enforced before the model is touched so an oversized request costs
/// nothing on the board.
/// </summary>
public sealed class BoundedChatClient : DelegatingChatClient
{
    private readonly InferenceGate _gate;
    private readonly InferenceLimits _limits;

    /// <summary>Creates the bounded client.</summary>
    public BoundedChatClient(IChatClient innerClient, InferenceGate gate, IOptions<InferenceLimits> limits)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(limits);
        _gate = gate;
        _limits = limits.Value;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = Validate(messages);
        var bounded = ApplyOutputBound(options);

        using var lease = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CreateTimeout(cancellationToken);

        return await base.GetResponseAsync(materialized, bounded, timeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = Validate(messages);
        var bounded = ApplyOutputBound(options);

        using var lease = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CreateTimeout(cancellationToken);

        var stream = base.GetStreamingResponseAsync(materialized, bounded, timeout.Token);
        await foreach (var update in stream.ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_limits.GenerationTimeout);
        return source;
    }

    private List<ChatMessage> Validate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages as List<ChatMessage> ?? [.. messages];

        long characters = 0;
        foreach (var message in materialized)
        {
            characters += message.Text.Length;
            if (characters > _limits.MaxPromptCharacters)
            {
                throw new PromptTooLargeException(characters, _limits.MaxPromptCharacters, "characters");
            }
        }

        return materialized;
    }

    private ChatOptions ApplyOutputBound(ChatOptions? options)
    {
        // Clone so a caller supplied options object is never mutated, and clamp rather
        // than reject because an over-large output request is a sensible default miss,
        // not a malformed request.
        var bounded = options?.Clone() ?? new ChatOptions();
        bounded.MaxOutputTokens = Math.Min(
            bounded.MaxOutputTokens ?? _limits.MaxOutputTokens,
            _limits.MaxOutputTokens);
        return bounded;
    }
}
