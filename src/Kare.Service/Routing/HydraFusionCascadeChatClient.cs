using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Kare.Core;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Inference.GenieX;
using Kare.Service.Cache;
using Kare.Service.Dashboard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kare.Service.Routing;

/// <summary>
/// Runs one bounded cascade: cached target, local Qwen answer-or-escalate gate,
/// then configured cloud models in priority order.
/// </summary>
public sealed class HydraFusionCascadeChatClient : IChatClient
{
    private const string AnswerMarker = "KARE_ANSWER:";
    private const string EscalateMarker = "KARE_ESCALATE:";
    private readonly IChatClient _local;
    private readonly ICloudInferenceBackend _cloud;
    private readonly ICloudModelCatalog _catalog;
    private readonly IRouteRecorder _recorder;
    private readonly ResponseCache _cache;
    private readonly CascadeRouteContext _routeContext;
    private readonly RoutePolicyOptions _options;
    private readonly BackendKind _localBackend;
    private readonly string _localModelId;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HydraFusionCascadeChatClient> _logger;

    public HydraFusionCascadeChatClient(
        IChatClient local,
        ICloudInferenceBackend cloud,
        ICloudModelCatalog catalog,
        IRouteRecorder recorder,
        ResponseCache cache,
        CascadeRouteContext routeContext,
        IOptions<RoutePolicyOptions> options,
        BackendKind localBackend,
        string localModelId,
        ILoggerFactory loggerFactory)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _routeContext = routeContext ?? throw new ArgumentNullException(nameof(routeContext));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _localBackend = localBackend;
        _localModelId = localModelId;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<HydraFusionCascadeChatClient>();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var requiresTools = RequiresTools(materialized, options);
        var candidates = GetCandidates(requiresTools);
        if (!_options.EnableCascadeEscalation)
        {
            return await CompleteLocalAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        }
        if (candidates.Count == 0)
        {
            if (requiresTools)
            {
                throw new UnsupportedBackendCapabilityException(
                    "tools",
                    "No configured cloud cascade target supports caller-owned tools.");
            }

            return await CompleteLocalAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        }

        var decisionMessages = CreateDecisionMessages(materialized, options, candidates);
        if (_cache.TryGetCascadeTarget(decisionMessages, options, candidates, out var cachedTarget))
        {
            return await CompleteCloudCascadeAsync(
                materialized,
                options,
                candidates,
                cachedTarget!,
                "A cached cascade route matched this request.",
                cancellationToken).ConfigureAwait(false);
        }

        var gate = await RunLocalGateAsync(
            decisionMessages,
            candidates,
            requiresTools,
            cancellationToken).ConfigureAwait(false);
        if (gate.Answer is not null && !requiresTools)
        {
            _routeContext.Current = gate.Decision;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, gate.Answer))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = gate.Response?.Usage,
            };
        }

        var target = gate.Target ?? candidates[0].Id;
        _cache.SetCascadeTarget(decisionMessages, options, candidates, target);
        return await CompleteCloudCascadeAsync(
            materialized,
            options,
            candidates,
            target,
            gate.Reason,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var requiresTools = RequiresTools(materialized, options);
        var candidates = GetCandidates(requiresTools);
        if (!_options.EnableCascadeEscalation)
        {
            await foreach (var update in StreamLocalAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }
        if (candidates.Count == 0)
        {
            if (requiresTools)
            {
                throw new UnsupportedBackendCapabilityException(
                    "tools",
                    "No configured cloud cascade target supports caller-owned tools.");
            }

            await foreach (var update in StreamLocalAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var decisionMessages = CreateDecisionMessages(materialized, options, candidates);
        string target;
        string reason;
        if (_cache.TryGetCascadeTarget(decisionMessages, options, candidates, out var cachedTarget))
        {
            target = cachedTarget!;
            reason = "A cached cascade route matched this request.";
        }
        else
        {
            var gate = await RunLocalGateAsync(
                decisionMessages,
                candidates,
                requiresTools,
                cancellationToken)
                .ConfigureAwait(false);
            if (gate.Answer is not null && !requiresTools)
            {
                _routeContext.Current = gate.Decision;
                yield return new ChatResponseUpdate(ChatRole.Assistant, gate.Answer)
                {
                    FinishReason = ChatFinishReason.Stop,
                };
                if (gate.Response?.Usage is { } usage)
                {
                    var usageUpdate = new ChatResponseUpdate();
                    usageUpdate.Contents.Add(new UsageContent(usage));
                    yield return usageUpdate;
                }

                yield break;
            }

            target = gate.Target ?? candidates[0].Id;
            reason = gate.Reason;
            _cache.SetCascadeTarget(decisionMessages, options, candidates, target);
        }

        await foreach (var update in StreamCloudCascadeAsync(
            materialized,
            options,
            candidates,
            target,
            reason,
            cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private async Task<LocalGateResult> RunLocalGateAsync(
        IReadOnlyList<ChatMessage> decisionMessages,
        IReadOnlyList<CloudModelDescriptor> candidates,
        bool requiresTools,
        CancellationToken cancellationToken)
    {
        var decision = LocalDecision(
            "HydraFusion cascade asked local Qwen to answer from authoritative context or select escalation.");
        var recording = CreateRecordingClient(_local, decision);
        var gateOptions = new ChatOptions
        {
            ModelId = _localModelId,
            Temperature = 0,
            MaxOutputTokens = _options.CascadeDecisionMaxOutputTokens,
            ToolMode = ChatToolMode.None,
            AdditionalProperties = new()
            {
                [GenieXBackend.DisableThinkingOptionName] = true,
                [ContextEnrichingChatClient.SkipKnowledgeContextOptionName] = requiresTools,
            },
        };

        try
        {
            var response = await recording
                .GetResponseAsync(decisionMessages, gateOptions, cancellationToken)
                .ConfigureAwait(false);
            var text = GetText(response).Trim();
            if (response.FinishReason != ChatFinishReason.Length &&
                text.StartsWith(AnswerMarker, StringComparison.Ordinal))
            {
                var answer = text[AnswerMarker.Length..].Trim();
                if (answer.Length > 0)
                {
                    return new LocalGateResult(
                        answer,
                        Target: null,
                        response,
                        decision,
                        "Local Qwen returned an authoritative answer.");
                }
            }

            var target = ParseTarget(text, candidates);
            return new LocalGateResult(
                Answer: null,
                target,
                response,
                decision,
                response.FinishReason == ChatFinishReason.Length
                    ? "Local Qwen's cascade answer was truncated; using the first configured cloud model."
                    : target is null
                    ? "Local Qwen requested escalation without a valid target; using the first configured cloud model."
                    : $"Local Qwen selected cascade target {target}.");
        }
        catch (Exception ex) when (
            ex is PromptTooLargeException or LocalInferenceException or
                UnsupportedBackendCapabilityException or InferenceCapacityException)
        {
            _logger.LogWarning(
                ex,
                "Local cascade gate failed; escalating to the first configured cloud model.");
            return new LocalGateResult(
                Answer: null,
                candidates[0].Id,
                Response: null,
                decision,
                "Local Qwen was unavailable, so the cascade selected the first configured cloud model.");
        }
    }

    private async Task<ChatResponse> CompleteCloudCascadeAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<CloudModelDescriptor> candidates,
        string target,
        string reason,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var candidate in CandidatesFrom(candidates, target))
        {
            var decision = CloudDecision(candidate, reason);
            _routeContext.Current = decision;
            try
            {
                var recording = CreateRecordingClient(_cloud, decision);
                var response = await recording
                    .GetResponseAsync(messages, CloudOptions(options, candidate), cancellationToken)
                    .ConfigureAwait(false);
                return response;
            }
            catch (Exception ex) when (
                ex is CloudInferenceException or NoBackendAvailableException or
                    UnsupportedBackendCapabilityException)
            {
                lastError = ex;
                reason = $"Cascade target {candidate.Id} failed before completion; trying the next configured model.";
                _logger.LogWarning(ex, "Cascade target {Target} failed.", candidate.Id);
            }
        }

        throw lastError ?? new NoBackendAvailableException("No configured cloud cascade target is available.");
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamCloudCascadeAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<CloudModelDescriptor> candidates,
        string target,
        string reason,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var candidate in CandidatesFrom(candidates, target))
        {
            var decision = CloudDecision(candidate, reason);
            _routeContext.Current = decision;
            var recording = CreateRecordingClient(_cloud, decision);
            await using var enumerator = recording
                .GetStreamingResponseAsync(messages, CloudOptions(options, candidate), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            ChatResponseUpdate first;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                first = enumerator.Current;
            }
            catch (Exception ex) when (
                ex is CloudInferenceException or NoBackendAvailableException or
                    UnsupportedBackendCapabilityException)
            {
                lastError = ex;
                reason = $"Cascade target {candidate.Id} failed before streaming; trying the next configured model.";
                _logger.LogWarning(ex, "Cascade target {Target} failed before streaming.", candidate.Id);
                continue;
            }

            yield return first;
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }

            yield break;
        }

        throw lastError ?? new NoBackendAvailableException("No configured cloud cascade target is available.");
    }

    private async Task<ChatResponse> CompleteLocalAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var decision = LocalDecision("Cascade escalation is disabled or no cloud models are configured.");
        _routeContext.Current = decision;
        return await CreateRecordingClient(_local, decision)
            .GetResponseAsync(messages, LocalOptions(options), cancellationToken)
            .ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamLocalAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var decision = LocalDecision("Cascade escalation is disabled or no cloud models are configured.");
        _routeContext.Current = decision;
        await foreach (var update in CreateRecordingClient(_local, decision)
            .GetStreamingResponseAsync(messages, LocalOptions(options), cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private IReadOnlyList<CloudModelDescriptor> GetCandidates(bool requiresTools) =>
        _catalog.Models
            .Where(model => !requiresTools || model.SupportsTools)
            .OrderBy(static model => model.Priority)
            .ThenBy(static model => model.Tier)
            .ThenBy(static model => model.Id, StringComparer.Ordinal)
            .ToArray();

    private static bool RequiresTools(IReadOnlyList<ChatMessage> messages, ChatOptions? options) =>
        options?.Tools is { Count: > 0 } ||
        messages.Any(static message =>
            message.Contents.Any(static content =>
                content is FunctionCallContent or FunctionResultContent));

    private IReadOnlyList<ChatMessage> CreateDecisionMessages(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<CloudModelDescriptor> candidates)
    {
        var request = GetLastUserText(messages);
        if (request.Length > _options.CascadeDecisionMaxInputCharacters)
        {
            request = request[^_options.CascadeDecisionMaxInputCharacters..];
        }

        var candidateList = string.Join(
            '\n',
            candidates.Select(static candidate =>
                $"- {candidate.Id}: {candidate.ModelId}; tier={candidate.Tier}; tools={candidate.SupportsTools}"));
        var toolCount = options?.Tools?.Count ?? 0;
        var toolNames = string.Join(
            ", ",
            options?.Tools?
                .OfType<AIFunctionDeclaration>()
                .Select(static tool => tool.Name)
                .Take(32) ?? []);
        var totalCharacters = CountTextCharacters(messages);
        _logger.LogDebug(
            "Cascade gate received {TotalCharacters} text characters across {MessageCount} messages and {ToolCount} tools; using {RequestCharacters} user-request characters.",
            totalCharacters,
            messages.Count,
            toolCount,
            request.Length);
        var instructions = $"""
            You are Kare's local Qwen cascade gate. Dashboard authoritative sources and enabled skills
            are supplied in a separate system message. Connected MCP server names and capabilities are
            advisory; do not claim to have called them.

            Return exactly one of these forms:
            KARE_ANSWER:
            <answer>

            KARE_ESCALATE:<target-id>

            Answer locally only when the supplied authoritative context directly supports the answer,
            or the request is simple and reliable without repository access or tools. Escalate coding,
            repository, tool, uncertain, or long-context work. Choose the earliest sufficient target.

            Request metadata: characters={totalCharacters}; messages={messages.Count}; tools={toolCount};
            tool names={toolNames}.
            Ordered cloud targets:
            {candidateList}
            """;

        return
        [
            new ChatMessage(ChatRole.System, instructions),
            new ChatMessage(ChatRole.User, request),
        ];
    }

    private static string GetLastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role != ChatRole.User)
            {
                continue;
            }

            var text = string.Join(
                "\n",
                messages[index].Contents.OfType<TextContent>().Select(static content => content.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return "No user text was supplied. Escalate to the first configured target.";
    }

    private static long CountTextCharacters(IReadOnlyList<ChatMessage> messages) =>
        messages.Sum(static message =>
            message.Contents.OfType<TextContent>().Sum(static content => (long)content.Text.Length));

    private static string GetText(ChatResponse response) =>
        string.Join(
            "\n",
            response.Messages.SelectMany(static message =>
                message.Contents.OfType<TextContent>().Select(static content => content.Text)));

    private static string? ParseTarget(
        string text,
        IReadOnlyList<CloudModelDescriptor> candidates)
    {
        if (!text.StartsWith(EscalateMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var requested = text[EscalateMarker.Length..]
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.ModelId, requested, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static IEnumerable<CloudModelDescriptor> CandidatesFrom(
        IReadOnlyList<CloudModelDescriptor> candidates,
        string target)
    {
        var index = candidates
            .Select((candidate, position) => (candidate, position))
            .FirstOrDefault(item =>
                string.Equals(item.candidate.Id, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.candidate.ModelId, target, StringComparison.OrdinalIgnoreCase))
            .position;
        return candidates.Skip(index);
    }

    private RouteRecordingChatClient CreateRecordingClient(IChatClient client, RouteDecision decision) =>
        new(
            client,
            decision,
            _recorder,
            _loggerFactory.CreateLogger<RouteRecordingChatClient>());

    private RouteDecision LocalDecision(string reason) =>
        new(
            KareRoute.LocalSlm,
            reason,
            _localModelId,
            _localBackend,
            IsBillable: false);

    private static RouteDecision CloudDecision(CloudModelDescriptor model, string reason) =>
        new(
            model.Route,
            reason,
            model.ModelId,
            BackendKind.Remote,
            IsBillable: true,
            FellBackFrom: KareRoute.LocalSlm,
            ProviderRouteId: model.Id);

    private ChatOptions LocalOptions(ChatOptions? options)
    {
        var selected = options?.Clone() ?? new ChatOptions();
        selected.ModelId = _localModelId;
        return selected;
    }

    private static ChatOptions CloudOptions(ChatOptions? options, CloudModelDescriptor candidate)
    {
        var selected = options?.Clone() ?? new ChatOptions();
        selected.ModelId = candidate.Id;
        if (!candidate.SupportsTools)
        {
            selected.Tools = null;
            selected.ToolMode = ChatToolMode.None;
        }

        return selected;
    }

    private sealed record LocalGateResult(
        string? Answer,
        string? Target,
        ChatResponse? Response,
        RouteDecision Decision,
        string Reason);
}
