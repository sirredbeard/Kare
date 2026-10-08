namespace Kare.Service.Dashboard;

/// <summary>
/// Metric types for dashboard display of routing decisions and system activity.
/// </summary>
public static class DashboardMetrics
{
    /// <summary>
    /// Single inference request routed through Kare.
    /// </summary>
    public record RequestMetric(
        string Id,
        DateTime Timestamp,
        string Route,
        string ModelId,
        string? ProviderRouteId,
        string Backend,
        bool IsBillable,
        bool IsFallback,
        bool Succeeded,
        double TimeToFirstTokenMs,
        double TotalDurationMs,
        long? InputTokens,
        long? OutputTokens,
        double? DecodeTokensPerSecond);

    /// <summary>
    /// Aggregated usage for one model, backend, and route combination.
    /// </summary>
    public sealed record ModelUsage(
        string ModelId,
        string? ProviderRouteId,
        string Backend,
        string Route,
        long RequestCount,
        long SuccessfulRequests,
        long FailedRequests,
        long BillableRequests,
        long FallbackRequests,
        long InputTokens,
        long OutputTokens,
        double AverageTimeToFirstTokenMs,
        double AverageTotalDurationMs,
        double? AverageDecodeTokensPerSecond,
        DateTime LastUsedAt,
        long PricedRequests,
        long PricedInputTokens,
        long PricedOutputTokens,
        long SuccessfulPricedRequests,
        long SuccessfulPricedInputTokens,
        long SuccessfulPricedOutputTokens);

    /// <summary>Configured model endpoint, observed request totals, and approximate cost metadata.</summary>
    public sealed record ModelEndpoint(
        string Id,
        string Provider,
        string ModelId,
        string? WireModel,
        string Endpoint,
        string Tier,
        bool SupportsTools,
        long RequestCount,
        long InputTokens,
        long OutputTokens,
        DateTime? LastUsedAt,
        decimal? AverageInputCostUsdPerMillionTokens = null,
        decimal? AverageOutputCostUsdPerMillionTokens = null,
        string? PricingSource = null,
        DateOnly? PricingAsOf = null,
        decimal? EstimatedCostUsd = null,
        long EstimatedCostRequests = 0,
        decimal? EstimatedAvoidedCostUsd = null,
        decimal? AverageEstimatedAvoidedCostPerRequestUsd = null,
        long EstimatedAvoidedCostRequests = 0);

    /// <summary>
    /// High-level activity: routing decision, fallback event, or service action.
    /// </summary>
    public record Activity(
        string Id,
        DateTime Timestamp,
        string Type,
        string Description,
        string Status,
        string Route,
        string Backend);

    /// <summary>
    /// Cache entry for dashboard view/delete operations. <see cref="Keywords"/> and
    /// <see cref="TaskClass"/> are a bounded intersection with a fixed vocabulary
    /// (<see cref="ContentClassifier"/>), computed from in-memory request/response text and
    /// never derived from or containing the original prompt or response content.
    /// </summary>
    public record CacheEntry(
        string Key,
        DateTime CreatedAt,
        DateTime? LastAccessedAt,
        long SizeBytes,
        string ContentType,
        IReadOnlyList<string>? Keywords = null,
        string? TaskClass = null,
        string? Name = null,
        string? Kind = null,
        DateTime? ExpiresAt = null,
        string? ModelId = null,
        string? Route = null,
        string? Backend = null,
        string? RouteTarget = null,
        long? InputTokens = null,
        long? OutputTokens = null,
        long? TotalTokens = null,
        string? Summary = null,
        string? ReuseHint = null);

    /// <summary>
    /// SLM workload snapshot.
    /// </summary>
    public record WorkloadSnapshot(
        int QueueDepth,
        int ActiveRequests,
        double BusyTimePercent,
        long TotalRequestsProcessed,
        double AverageTimeToFirstTokenMs);

    /// <summary>
    /// Authoritative web source crawled for local model context. <see cref="Topics"/> and
    /// <see cref="Headings"/> are a bounded table-of-contents style descriptor so the route
    /// gate can recognize a source exists even when its full content is not selected for
    /// injection into the current request.
    /// </summary>
    public record RoutingDecisionUrl(
        string Id,
        string Pattern,
        bool Enabled,
        DateTime CreatedAt,
        DateTime LastModifiedAt,
        DateTime? LastCrawledAt,
        int PageCount,
        int ContentCharacters,
        string Status,
        string? Error,
        IReadOnlyList<string>? Topics = null,
        IReadOnlyList<string>? Headings = null);

    /// <summary>
    /// Skill available to SLM. <see cref="Tags"/> is a bounded language/framework/task
    /// descriptor used for progressive disclosure: only skills whose tags or keywords match
    /// the current request are selected for full-body injection.
    /// </summary>
    public record SkillInfo(
        string Name,
        string Path,
        string Description,
        bool Enabled,
        long SizeBytes,
        DateTime LastModifiedAt,
        string Status,
        IReadOnlyList<string>? Tags = null);

    /// <summary>
    /// MCP server linked to SLM. <see cref="Keywords"/> is a bounded task/capability
    /// descriptor derived from advertised capabilities and tool names so a larger registry
    /// can be filtered by request relevance instead of injecting every connected server.
    /// </summary>
    public record McpServerInfo(
        string Name,
        string Endpoint,
        string[] Capabilities,
        bool Connected,
        DateTime? LastConnectedAt,
        DateTime? LastCheckedAt,
        string? Error,
        IReadOnlyList<McpToolInfo>? Tools = null,
        IReadOnlyList<string>? Keywords = null);


    /// <summary>Bounded MCP tool metadata used for request-specific knowledge selection.</summary>
    public record McpToolInfo(
        string Name,
        string Description,
        string InputSchema);

    /// <summary>
    /// Complete point-in-time dashboard payload.
    /// </summary>
    public record Snapshot(
        DateTime GeneratedAt,
        IReadOnlyList<ModelEndpoint> ModelEndpoints,
        IReadOnlyList<ModelUsage> ModelUsage,
        IReadOnlyList<RequestMetric> Requests,
        IReadOnlyList<Activity> Activities,
        IReadOnlyList<CacheEntry> CacheEntries,
        WorkloadSnapshot? Workload,
        IReadOnlyList<RoutingDecisionUrl> RoutingDecisionUrls,
        IReadOnlyList<SkillInfo> Skills,
        IReadOnlyList<McpServerInfo> McpServers);
}
