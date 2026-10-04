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
        DateTime LastUsedAt);

    /// <summary>Configured model endpoint and its observed request totals.</summary>
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
        DateTime? LastUsedAt);

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
    /// Cache entry for dashboard view/delete operations.
    /// </summary>
    public record CacheEntry(
        string Key,
        DateTime CreatedAt,
        DateTime? LastAccessedAt,
        long SizeBytes,
        string ContentType);

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
    /// Authoritative web source crawled for local model context.
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
        string? Error);

    /// <summary>
    /// Skill available to SLM.
    /// </summary>
    public record SkillInfo(
        string Name,
        string Path,
        string Description,
        bool Enabled,
        long SizeBytes,
        DateTime LastModifiedAt,
        string Status);

    /// <summary>
    /// MCP server linked to SLM.
    /// </summary>
    public record McpServerInfo(
        string Name,
        string Endpoint,
        string[] Capabilities,
        bool Connected,
        DateTime? LastConnectedAt,
        DateTime? LastCheckedAt,
        string? Error);

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
