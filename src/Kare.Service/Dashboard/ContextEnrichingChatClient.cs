using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Kare.Service.Dashboard;

/// <summary>Adds bounded Kare-managed source and skill context to local inference only.</summary>
public sealed class ContextEnrichingChatClient : IChatClient
{
    public const string SkipKnowledgeContextOptionName = "kare.context.skip-knowledge";

    private readonly IChatClient _inner;
    private readonly IDashboardKnowledgeService _knowledge;

    public ContextEnrichingChatClient(IChatClient inner, IDashboardKnowledgeService knowledge)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        if (ShouldSkipKnowledge(options))
        {
            return await _inner.GetResponseAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var enriched = await _knowledge
            .AddLocalContextAsync(materialized, cancellationToken)
            .ConfigureAwait(false);
        return await _inner.GetResponseAsync(enriched, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        if (ShouldSkipKnowledge(options))
        {
            await foreach (var update in _inner
                .GetStreamingResponseAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var enriched = await _knowledge
            .AddLocalContextAsync(materialized, cancellationToken)
            .ConfigureAwait(false);

        await foreach (var update in _inner
            .GetStreamingResponseAsync(enriched, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private static bool ShouldSkipKnowledge(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(
            SkipKnowledgeContextOptionName,
            out var skip) == true &&
        skip is true;
}
