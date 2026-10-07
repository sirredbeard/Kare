using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Kare.Service.Routing;

/// <summary>Adds one Copilot-visible warning for each local degradation episode.</summary>
public sealed class LocalBackendWarningChatClient : DelegatingChatClient
{
    internal const string WarningPrefix = "! Kare's local NPU is unavailable";
    private readonly SelectedBackend _selected;

    public LocalBackendWarningChatClient(IChatClient innerClient, SelectedBackend selected)
        : base(innerClient)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        if (_selected.TryTakeWarning(out var warning))
        {
            var assistant = response.Messages.FirstOrDefault(static message =>
                message.Role == ChatRole.Assistant);
            if (assistant is null)
            {
                response.Messages.Insert(0, new ChatMessage(ChatRole.Assistant, warning));
            }
            else
            {
                assistant.Contents.Insert(0, new TextContent(warning + Environment.NewLine + Environment.NewLine));
            }
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            yield break;
        }

        if (_selected.TryTakeWarning(out var warning))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, warning + Environment.NewLine + Environment.NewLine);
        }

        yield return enumerator.Current;
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            yield return enumerator.Current;
        }
    }

    internal static bool IsWarningResponse(ChatResponse response) =>
        response.Messages.Any(static message =>
            message.Contents.OfType<TextContent>().Any(static content =>
                content.Text.StartsWith(WarningPrefix, StringComparison.Ordinal)));
}
