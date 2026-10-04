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
        string Backend,
        bool IsBillable,
        bool IsFallback,
        bool Succeeded,
        double TimeToFirstTokenMs,
        double TotalDurationMs,
        int OutputTokens,
        double DecodeTokensPerSecond);

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
    /// URL registry entry for local vs cloud routing decision thresholds.
    /// </summary>
    public record RoutingDecisionUrl(
        string Url,
        bool PreferLocal,
        DateTime CreatedAt,
        DateTime LastModifiedAt);

    /// <summary>
    /// Skill available to SLM.
    /// </summary>
    public record SkillInfo(
        string Name,
        string Path,
        string Description,
        DateTime LastModifiedAt);

    /// <summary>
    /// MCP server linked to SLM.
    /// </summary>
    public record McpServerInfo(
        string Name,
        string Endpoint,
        string[] Capabilities,
        bool Connected,
        DateTime LastConnectedAt);
}
