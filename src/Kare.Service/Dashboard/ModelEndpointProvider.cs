using Kare.Abstractions;
using Kare.Cloud.Copilot;
using Kare.Inference.GenieX;
using Microsoft.Extensions.Options;

namespace Kare.Service.Dashboard;

/// <summary>Builds the dashboard view of configured local and cloud model endpoints.</summary>
public interface IModelEndpointProvider
{
    IReadOnlyList<DashboardMetrics.ModelEndpoint> GetEndpoints(
        IReadOnlyList<DashboardMetrics.ModelUsage> usage);
}

/// <summary>Configuration-backed model endpoint provider.</summary>
public sealed class ModelEndpointProvider : IModelEndpointProvider
{
    private readonly SelectedBackend _selectedBackend;
    private readonly GenieXOptions _genieX;
    private readonly CopilotSdkOptions _cloud;

    public ModelEndpointProvider(
        SelectedBackend selectedBackend,
        IOptions<GenieXOptions> genieX,
        IOptions<CopilotSdkOptions> cloud)
    {
        ArgumentNullException.ThrowIfNull(selectedBackend);
        ArgumentNullException.ThrowIfNull(genieX);
        ArgumentNullException.ThrowIfNull(cloud);
        _selectedBackend = selectedBackend;
        _genieX = genieX.Value;
        _cloud = cloud.Value;
    }

    public IReadOnlyList<DashboardMetrics.ModelEndpoint> GetEndpoints(
        IReadOnlyList<DashboardMetrics.ModelUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var endpoints = new List<DashboardMetrics.ModelEndpoint>
        {
            CreateLocal(usage),
        };

        endpoints.AddRange(_cloud.Models.Select(model => CreateCloud(model, usage)));
        return endpoints;
    }

    private DashboardMetrics.ModelEndpoint CreateLocal(
        IReadOnlyList<DashboardMetrics.ModelUsage> usage)
    {
        var modelId = _selectedBackend.ModelId;
        var observed = usage.Where(item =>
            item.ProviderRouteId is null &&
            !string.Equals(item.Backend, BackendKind.Remote.ToString(), StringComparison.Ordinal));
        CloudModelRouteOptions[] pricedModels = _cloud.Enabled
            ? _cloud.Models
                .Where(static model =>
                    model.AverageInputCostUsdPerMillionTokens is not null &&
                    model.AverageOutputCostUsdPerMillionTokens is not null)
                .ToArray()
            : [];
        decimal? averageInputPrice = pricedModels.Length == 0
            ? null
            : pricedModels.Average(static model => model.AverageInputCostUsdPerMillionTokens!.Value);
        decimal? averageOutputPrice = pricedModels.Length == 0
            ? null
            : pricedModels.Average(static model => model.AverageOutputCostUsdPerMillionTokens!.Value);

        return Create(
            "local",
            _selectedBackend.Kind.ToString(),
            modelId,
            wireModel: modelId,
            _selectedBackend.Kind == BackendKind.GenieXQairt
                ? _genieX.Endpoint.ToString()
                : "in-process://onnx-genai",
            "Local",
            supportsTools: false,
            observed,
            averageInputPrice,
            averageOutputPrice,
            pricingSource: null,
            pricingAsOf: null,
            estimateAvoidedCost: true,
            averagePriceModelCount: pricedModels.Length);
    }

    private static DashboardMetrics.ModelEndpoint CreateCloud(
        CloudModelRouteOptions model,
        IReadOnlyList<DashboardMetrics.ModelUsage> usage)
    {
        var observed = usage.Where(item =>
            string.Equals(item.ProviderRouteId, model.Id, StringComparison.Ordinal));
        var endpoint = model.Provider == CloudModelProvider.GitHubCopilot
            ? "copilot-sdk://github"
            : model.BaseUrl ?? "unconfigured";

        return Create(
            model.Id,
            model.Provider.ToString(),
            model.ModelId,
            model.WireModel,
            endpoint,
            model.Tier.ToString(),
            model.SupportsTools,
            observed,
            model.AverageInputCostUsdPerMillionTokens,
            model.AverageOutputCostUsdPerMillionTokens,
            model.PricingSource,
            model.PricingAsOf,
            estimateAvoidedCost: false);
    }

    private static DashboardMetrics.ModelEndpoint Create(
        string id,
        string provider,
        string modelId,
        string? wireModel,
        string endpoint,
        string tier,
        bool supportsTools,
        IEnumerable<DashboardMetrics.ModelUsage> observed,
        decimal? averageInputPrice,
        decimal? averageOutputPrice,
        string? pricingSource,
        DateOnly? pricingAsOf,
        bool estimateAvoidedCost,
        int? averagePriceModelCount = null)
    {
        var rows = observed.ToArray();
        var inputTokens = rows.Sum(static item => item.PricedInputTokens);
        var outputTokens = rows.Sum(static item => item.PricedOutputTokens);
        var pricedRequests = rows.Sum(static item => item.PricedRequests);
        var successfulPricedInputTokens = rows.Sum(static item => item.SuccessfulPricedInputTokens);
        var successfulPricedOutputTokens = rows.Sum(static item => item.SuccessfulPricedOutputTokens);
        var successfulPricedRequests = rows.Sum(static item => item.SuccessfulPricedRequests);
        var hasPrices = averageInputPrice is not null && averageOutputPrice is not null;
        var estimatedCost = !estimateAvoidedCost && hasPrices && pricedRequests > 0
            ? EstimateCost(inputTokens, outputTokens, averageInputPrice!.Value, averageOutputPrice!.Value)
            : null;
        var estimatedAvoidedCost = estimateAvoidedCost &&
            hasPrices &&
            successfulPricedRequests > 0
                ? EstimateCost(
                    successfulPricedInputTokens,
                    successfulPricedOutputTokens,
                    averageInputPrice!.Value,
                    averageOutputPrice!.Value)
                : null;

        return new DashboardMetrics.ModelEndpoint(
            id,
            provider,
            modelId,
            wireModel,
            endpoint,
            tier,
            supportsTools,
            rows.Sum(static item => item.RequestCount),
            rows.Sum(static item => item.InputTokens),
            rows.Sum(static item => item.OutputTokens),
            rows.Length == 0 ? null : rows.Max(static item => item.LastUsedAt),
            estimateAvoidedCost ? null : averageInputPrice,
            estimateAvoidedCost ? null : averageOutputPrice,
            pricingSource,
            pricingAsOf,
            estimatedCost,
            !estimateAvoidedCost && hasPrices ? pricedRequests : 0,
            estimatedAvoidedCost,
            estimatedAvoidedCost is null
                ? null
                : estimatedAvoidedCost.Value / successfulPricedRequests,
            estimateAvoidedCost && hasPrices ? successfulPricedRequests : 0,
            averagePriceModelCount);
    }

    private static decimal EstimateCost(
        long inputTokens,
        long outputTokens,
        decimal inputPrice,
        decimal outputPrice) =>
        ((inputTokens * inputPrice) + (outputTokens * outputPrice)) / 1_000_000m;
}
