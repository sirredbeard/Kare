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
        var requiresImages = RequiresImages(materialized);
        var candidates = GetCandidates(requiresTools, requiresImages);
        if (!_options.EnableCascadeEscalation)
        {
            if (requiresImages)
            {
                throw new UnsupportedBackendCapabilityException(
                    "images",
                    "Image requests require an enabled image-capable cloud cascade route.");
            }

            return await CompleteLocalAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        }
        if (candidates.Count == 0)
        {
            if (requiresTools || requiresImages)
            {
                throw new UnsupportedBackendCapabilityException(
                    requiresImages ? "images" : "tools",
                    "No configured cloud cascade target supports this request's capabilities.");
            }

            return await CompleteLocalAsync(materialized, options, cancellationToken).ConfigureAwait(false);
        }
        if (requiresImages)
        {
            return await CompleteCloudCascadeAsync(
                materialized,
                options,
                candidates,
                candidates[0].Id,
                "Image content requires an explicitly image-capable cloud model.",
                cancellationToken).ConfigureAwait(false);
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
        var requiresImages = RequiresImages(materialized);
        var candidates = GetCandidates(requiresTools, requiresImages);
        if (!_options.EnableCascadeEscalation)
        {
            if (requiresImages)
            {
                throw new UnsupportedBackendCapabilityException(
                    "images",
                    "Image requests require an enabled image-capable cloud cascade route.");
            }

            await foreach (var update in StreamLocalAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }
        if (candidates.Count == 0)
        {
            if (requiresTools || requiresImages)
            {
                throw new UnsupportedBackendCapabilityException(
                    requiresImages ? "images" : "tools",
                    "No configured cloud cascade target supports this request's capabilities.");
            }

            await foreach (var update in StreamLocalAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }
        if (requiresImages)
        {
            await foreach (var update in StreamCloudCascadeAsync(
                materialized,
                options,
                candidates,
                candidates[0].Id,
                "Image content requires an explicitly image-capable cloud model.",
                cancellationToken).ConfigureAwait(false))
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
                if (!await ShouldAcceptDraftAsync(
                        messages,
                        response,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    lastError = new CloudInferenceException(
                        $"The local result judge rejected cloud target {candidate.Id}.");
                    reason = $"The local result judge rejected {candidate.Id}; trying the next configured model.";
                    continue;
                }

                return await MaybeCritiqueAndReviseAsync(
                        messages,
                        options,
                        response,
                        candidate,
                        candidates,
                        cancellationToken)
                    .ConfigureAwait(false);
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

    private async Task<ChatResponse> MaybeCritiqueAndReviseAsync(
            IReadOnlyList<ChatMessage> request,
            ChatOptions? options,
            ChatResponse draft,
            CloudModelDescriptor drafter,
            IReadOnlyList<CloudModelDescriptor> candidates,
            CancellationToken cancellationToken)
        {
            if (!_options.EnableCascadeCritique ||
                options?.Tools is { Count: > 0 } ||
                draft.Messages.Any(message =>
                    message.Contents.Any(content =>
                        content is FunctionCallContent or FunctionResultContent)))
            {
                return draft;
            }

            var critic = candidates.FirstOrDefault(candidate =>
                candidate.Provider != drafter.Provider &&
                !string.Equals(candidate.Id, drafter.Id, StringComparison.Ordinal));
            if (critic is null)
            {
                _logger.LogInformation(
                    "Critique skipped for {Target}; no independent provider target is configured.",
                    drafter.Id);
                return draft;
            }

            var requestText = Limit(
                GetLastUserText(request),
                _options.CascadeCritiqueMaxInputCharacters);
            var draftText = Limit(
                GetText(draft),
                _options.CascadeCritiqueMaxInputCharacters);
            var criticMessages = new[]
            {
                new ChatMessage(
                    ChatRole.System,
                    """
                    You are Kare's read-only critique model. Review the request and draft.
                    Return only one compact result:
                    KARE_CRITIQUE:ACCEPT
                    or
                    KARE_CRITIQUE:REVISE:<specific reason>
                    Do not rewrite the answer, call tools, or propose a patch.
                    """),
                new ChatMessage(
                    ChatRole.User,
                    $"Request:\n{requestText}\n\nDraft from {drafter.ModelId}:\n{draftText}"),
            };
            var criticOptions = new ChatOptions
            {
                ModelId = critic.Id,
                Temperature = 0,
                MaxOutputTokens = _options.CascadeCritiqueMaxOutputTokens,
                ToolMode = ChatToolMode.None,
            };

            ChatResponse critique;
            try
            {
                critique = await CreateRecordingClient(
                        _cloud,
                        CloudDecision(
                            critic,
                            $"Read-only critique of draft from {drafter.Id}."))
                    .GetResponseAsync(criticMessages, criticOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is CloudInferenceException or NoBackendAvailableException or
                    UnsupportedBackendCapabilityException)
            {
                _logger.LogWarning(ex, "Critique failed for cloud target {Target}.", drafter.Id);
                _routeContext.Current = CloudDecision(
                    drafter,
                    $"Critique failed after draft from {drafter.Id}.");
                return draft;
            }

            var critiqueText = GetText(critique).Trim();
            if (critiqueText.StartsWith("KARE_CRITIQUE:ACCEPT", StringComparison.Ordinal))
            {
                _routeContext.Current = CloudDecision(
                    drafter,
                    $"Cloud draft accepted after critique from {critic.Id}.");
                return draft;
            }

            if (!critiqueText.StartsWith("KARE_CRITIQUE:REVISE:", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Critique returned an invalid verdict for cloud target {Target}.",
                    drafter.Id);
                _routeContext.Current = CloudDecision(
                    drafter,
                    $"Critique returned an invalid verdict for draft from {drafter.Id}.");
                return draft;
            }

            var revisionMessages = new List<ChatMessage>
            {
                new(
                    ChatRole.System,
                    """
                    Revise the draft once to complete the original request. Preserve correct work
                    and address the critic's substantive feedback. Return only the revised answer.
                    Do not mention this critique handoff and do not call tools.
                    """),
            };
            revisionMessages.AddRange(request);
            revisionMessages.Add(new ChatMessage(ChatRole.Assistant, draftText));
            revisionMessages.Add(new ChatMessage(ChatRole.User, $"Critic feedback:\n{critiqueText}"));

            try
            {
                var revised = await CreateRecordingClient(
                        _cloud,
                        CloudDecision(
                            drafter,
                            $"One revision after critique from {critic.Id}."))
                    .GetResponseAsync(
                        revisionMessages,
                        CritiqueRevisionOptions(options, drafter),
                        cancellationToken)
                    .ConfigureAwait(false);
                _routeContext.Current = CloudDecision(
                    drafter,
                    $"Cloud draft revised after critique from {critic.Id}.");
                return revised;
            }
            catch (Exception ex) when (
                ex is CloudInferenceException or NoBackendAvailableException or
                    UnsupportedBackendCapabilityException)
            {
                _logger.LogWarning(ex, "Critique revision failed for cloud target {Target}.", drafter.Id);
                _routeContext.Current = CloudDecision(
                    drafter,
                    $"Critique revision failed after draft from {drafter.Id}.");
                return draft;
            }
        }

    private async Task<bool> ShouldAcceptDraftAsync(
        IReadOnlyList<ChatMessage> request,
        ChatResponse response,
        CloudModelDescriptor candidate,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableCascadeResultJudge)
        {
            return true;
        }

        if (response.FinishReason == ChatFinishReason.Length)
        {
            return false;
        }

        if (response.Messages.Any(message =>
                message.Contents.Any(content =>
                    content is FunctionCallContent or FunctionResultContent)))
        {
            return true;
        }

        var draft = GetText(response);
        if (string.IsNullOrWhiteSpace(draft))
        {
            return false;
        }

        var requestText = GetLastUserText(request);
        if (requestText.Length > _options.CascadeDecisionMaxInputCharacters)
        {
            requestText = requestText[^_options.CascadeDecisionMaxInputCharacters..];
        }

        var draftText = draft.Length > _options.CascadeDecisionMaxInputCharacters
            ? draft[.._options.CascadeDecisionMaxInputCharacters]
            : draft;
        var judgeMessages = new[]
        {
            new ChatMessage(
                ChatRole.System,
                """
                You are Kare's bounded local result judge. Review only the compact request and draft.
                Return exactly KARE_ACCEPT or KARE_REPAIR:<reason>.
                Accept a complete, directly useful answer. Request repair for an incomplete, uncertain,
                unsupported, or obviously irrelevant answer. Do not provide advice or rewrite the answer.
                """),
            new ChatMessage(
                ChatRole.User,
                $"Request:\n{requestText}\n\nDraft from {candidate.ModelId}:\n{draftText}"),
        };
        var judgeOptions = new ChatOptions
        {
            ModelId = _localModelId,
            Temperature = 0,
            MaxOutputTokens = _options.CascadeJudgeMaxOutputTokens,
            ToolMode = ChatToolMode.None,
            AdditionalProperties = new()
            {
                [GenieXBackend.DisableThinkingOptionName] = true,
                [ContextEnrichingChatClient.SkipKnowledgeContextOptionName] = true,
            },
        };

        try
        {
            var judge = await CreateRecordingClient(
                    _local,
                    LocalDecision($"Judging the draft returned by {candidate.Id}."))
                .GetResponseAsync(judgeMessages, judgeOptions, cancellationToken)
                .ConfigureAwait(false);
            var verdict = GetText(judge).Trim();
            return verdict.StartsWith("KARE_ACCEPT", StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is PromptTooLargeException or LocalInferenceException or
                UnsupportedBackendCapabilityException or InferenceCapacityException)
        {
            _logger.LogWarning(
                ex,
                "Local result judge failed for cloud target {Target}; rejecting the draft.",
                candidate.Id);
            return false;
        }
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

    private IReadOnlyList<CloudModelDescriptor> GetCandidates(
        bool requiresTools,
        bool requiresImages) =>
        _catalog.Models
            .Where(model =>
                (!requiresTools || model.SupportsTools) &&
                (!requiresImages || model.SupportsImages))
            .OrderBy(static model => model.Priority)
            .ThenBy(static model => model.Tier)
            .ThenBy(static model => model.Id, StringComparer.Ordinal)
            .ToArray();

    private static bool RequiresTools(IReadOnlyList<ChatMessage> messages, ChatOptions? options) =>
        options?.Tools is { Count: > 0 } ||
        messages.Any(static message =>
            message.Contents.Any(static content =>
                content is FunctionCallContent or FunctionResultContent));

    private static bool RequiresImages(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(static message =>
            message.Contents.OfType<DataContent>()
                .Any(static content => content.HasTopLevelMediaType("image")));

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
                $"- {candidate.Id}: {candidate.ModelId}; tier={candidate.Tier}; tools={candidate.SupportsTools}; images={candidate.SupportsImages}"));
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

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

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

    private static ChatOptions CritiqueRevisionOptions(
        ChatOptions? options,
        CloudModelDescriptor candidate)
    {
        var selected = CloudOptions(options, candidate);
        selected.Tools = null;
        selected.ToolMode = ChatToolMode.None;
        return selected;
    }

    private sealed record LocalGateResult(
        string? Answer,
        string? Target,
        ChatResponse? Response,
        RouteDecision Decision,
        string Reason);
}
