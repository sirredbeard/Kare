using Kare.Abstractions;
using Kare.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Kare.Core.Routing;

/// <summary>
/// Selects local, Copilot, or Foundry from explicit wire model names and deterministic
/// request-shape policy. It never calls a model to decide whether to spend cloud credits.
/// </summary>
public sealed class ConfiguredRouteSelector : IRouteSelector
{
    private readonly RoutePolicyOptions _options;
    private readonly BackendKind _localBackend;
    private readonly string _localModelId;
    private readonly ICloudModelCatalog _catalog;

    /// <summary>Creates the configured selector.</summary>
    public ConfiguredRouteSelector(
        IOptions<RoutePolicyOptions> options,
        BackendKind localBackend,
        string localModelId,
        ICloudModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(localModelId);
        ArgumentNullException.ThrowIfNull(catalog);
        _options = options.Value;
        _localBackend = localBackend;
        _localModelId = localModelId;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public ValueTask<RouteDecision> SelectAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var requestedModel = options?.ModelId;
        var shape = Measure(messages, options);

        if (string.Equals(requestedModel, _options.LightModelId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(LightDecision(
                shape,
                "The caller explicitly selected the low-cost Copilot route."));
        }

        if (string.Equals(requestedModel, _options.CloudModelId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(HeavyDecision(
                shape,
                "The caller explicitly selected the heavy cloud route."));
        }

        if (string.Equals(requestedModel, _options.ComplexModelId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ComplexDecision(
                shape,
                "The caller explicitly selected the complex orchestration route."));
        }

        if (string.Equals(requestedModel, _options.AutomaticModelId, StringComparison.Ordinal) &&
            _options.EnableAutomaticCloudEscalation)
        {
            if (shape.Characters >= _options.ComplexPromptCharacterThreshold)
            {
                return ValueTask.FromResult(ComplexDecision(
                    shape,
                    $"The automatic route selected the complex tier because the request reached the configured {_options.ComplexPromptCharacterThreshold}-character threshold."));
            }

            if (shape.Characters >= _options.ModeratePromptCharacterThreshold ||
                shape.ToolCount > 0 ||
                shape.MessageCount >= 8)
            {
                return ValueTask.FromResult(LightDecision(
                    shape,
                    "The automatic route selected the moderate tier from the deterministic request-shape policy."));
            }
        }

        var reason = string.Equals(requestedModel, _options.AutomaticModelId, StringComparison.Ordinal)
            ? "The automatic route selected the local SLM under the configured complexity thresholds."
            : "The caller selected the local route.";

        return ValueTask.FromResult(new RouteDecision(
            KareRoute.LocalSlm,
            reason,
            _localModelId,
            _localBackend,
            IsBillable: false));
    }

    private RouteDecision LightDecision(RequestShape shape, string reason)
    {
        var candidates = Candidates(CloudModelTier.Fast, shape);
        var index = shape.ToolCount > 0 || shape.MessageCount >= 8
            ? candidates.Count - 1
            : Math.Min(
                candidates.Count - 1,
                (int)(shape.Characters / Math.Max(1, _options.ModeratePromptCharacterThreshold)));
        return CloudDecision(candidates[index], reason);
    }

    private RouteDecision HeavyDecision(RequestShape shape, string reason) =>
        CloudDecision(Candidates(CloudModelTier.Heavy, shape)[0], reason);

    private RouteDecision ComplexDecision(RequestShape shape, string reason) =>
        CloudDecision(Candidates(CloudModelTier.Complex, shape)[0], reason);

    private IReadOnlyList<CloudModelDescriptor> Candidates(
        CloudModelTier tier,
        RequestShape shape)
    {
        var candidates = _catalog.Models
            .Where(model => model.Tier == tier &&
                (shape.ToolCount == 0 || model.SupportsTools))
            .OrderBy(static model => model.Priority)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new NoBackendAvailableException(
                $"No configured {tier} cloud model supports this request.");
        }

        return candidates;
    }

    private static RouteDecision CloudDecision(
        CloudModelDescriptor model,
        string reason) => new(
            model.Route,
            reason,
            model.ModelId,
            BackendKind.Remote,
            IsBillable: true,
            ProviderRouteId: model.Id);

    private static RequestShape Measure(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options)
    {
        long count = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    count += text.Text.Length;
                }
            }
        }

        return new RequestShape(
            count,
            messages.Count,
            options?.Tools?.Count ?? 0);
    }

    private readonly record struct RequestShape(
        long Characters,
        int MessageCount,
        int ToolCount);
}
