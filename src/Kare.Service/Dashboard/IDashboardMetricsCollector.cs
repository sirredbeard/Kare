namespace Kare.Service.Dashboard;

/// <summary>
/// Collects metrics for dashboard display.
/// Implementations track requests, activities, cache state, workload, routing decisions, skills, and MCP servers.
/// </summary>
public interface IDashboardMetricsCollector
{
    void RecordRequest(DashboardMetrics.RequestMetric request);
    void RecordActivity(DashboardMetrics.Activity activity);
    void RecordCacheEntry(DashboardMetrics.CacheEntry entry);
    void RemoveCacheEntry(string key);
    void UpdateWorkload(DashboardMetrics.WorkloadSnapshot workload);
    void RegisterRoutingDecisionUrl(DashboardMetrics.RoutingDecisionUrl url);
    void RegisterSkill(DashboardMetrics.SkillInfo skill);
    void RegisterMcpServer(DashboardMetrics.McpServerInfo server);

    IReadOnlyList<DashboardMetrics.RequestMetric> GetRequests();
    IReadOnlyList<DashboardMetrics.Activity> GetActivities();
    IReadOnlyList<DashboardMetrics.CacheEntry> GetCacheEntries();
    DashboardMetrics.WorkloadSnapshot? GetWorkload();
    IReadOnlyList<DashboardMetrics.RoutingDecisionUrl> GetRoutingDecisionUrls();
    IReadOnlyList<DashboardMetrics.SkillInfo> GetSkills();
    IReadOnlyList<DashboardMetrics.McpServerInfo> GetMcpServers();
}
