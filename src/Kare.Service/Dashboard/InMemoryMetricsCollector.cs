namespace Kare.Service.Dashboard;

/// <summary>
/// Bounded, process-local dashboard state. It stores metadata only, never prompts or responses.
/// </summary>
public sealed class InMemoryMetricsCollector : IDashboardMetricsCollector
{
    private const int MaxRequests = 500;
    private const int MaxActivities = 500;
    private const int MaxRegistryEntries = 500;

    private readonly Lock _sync = new();
    private readonly List<DashboardMetrics.RequestMetric> _requests = [];
    private readonly List<DashboardMetrics.Activity> _activities = [];
    private readonly Dictionary<string, DashboardMetrics.CacheEntry> _cacheEntries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DashboardMetrics.RoutingDecisionUrl> _routingDecisionUrls =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DashboardMetrics.SkillInfo> _skills =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DashboardMetrics.McpServerInfo> _mcpServers =
        new(StringComparer.Ordinal);
    private DashboardMetrics.WorkloadSnapshot? _workload;

    public void RecordRequest(DashboardMetrics.RequestMetric request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            AddNewest(_requests, request, MaxRequests);
        }
    }

    public void RecordActivity(DashboardMetrics.Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (_sync)
        {
            AddNewest(_activities, activity, MaxActivities);
        }
    }

    public void RecordCacheEntry(DashboardMetrics.CacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            if (_cacheEntries.TryGetValue(entry.Key, out var existing))
            {
                entry = entry with { CreatedAt = existing.CreatedAt };
            }

            _cacheEntries[entry.Key] = entry;
            TrimOldest(_cacheEntries, MaxRegistryEntries, static value => value.CreatedAt);
        }
    }

    public void RemoveCacheEntry(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            _cacheEntries.Remove(key);
        }
    }

    public void UpdateWorkload(DashboardMetrics.WorkloadSnapshot workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        lock (_sync)
        {
            _workload = workload;
        }
    }

    public void RegisterRoutingDecisionUrl(DashboardMetrics.RoutingDecisionUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);
        lock (_sync)
        {
            _routingDecisionUrls[url.Url] = url;
            TrimOldest(_routingDecisionUrls, MaxRegistryEntries, static value => value.LastModifiedAt);
        }
    }

    public void RegisterSkill(DashboardMetrics.SkillInfo skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        lock (_sync)
        {
            _skills[skill.Name] = skill;
            TrimOldest(_skills, MaxRegistryEntries, static value => value.LastModifiedAt);
        }
    }

    public void RegisterMcpServer(DashboardMetrics.McpServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_sync)
        {
            _mcpServers[server.Name] = server;
            TrimOldest(_mcpServers, MaxRegistryEntries, static value => value.LastConnectedAt);
        }
    }

    public IReadOnlyList<DashboardMetrics.RequestMetric> GetRequests()
    {
        lock (_sync)
        {
            return [.. _requests];
        }
    }

    public IReadOnlyList<DashboardMetrics.Activity> GetActivities()
    {
        lock (_sync)
        {
            return [.. _activities];
        }
    }

    public IReadOnlyList<DashboardMetrics.CacheEntry> GetCacheEntries()
    {
        lock (_sync)
        {
            return [.. _cacheEntries.Values.OrderByDescending(static entry => entry.CreatedAt)];
        }
    }

    public DashboardMetrics.WorkloadSnapshot? GetWorkload()
    {
        lock (_sync)
        {
            return _workload;
        }
    }

    public IReadOnlyList<DashboardMetrics.RoutingDecisionUrl> GetRoutingDecisionUrls()
    {
        lock (_sync)
        {
            return [.. _routingDecisionUrls.Values.OrderBy(static entry => entry.Url, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<DashboardMetrics.SkillInfo> GetSkills()
    {
        lock (_sync)
        {
            return [.. _skills.Values.OrderBy(static entry => entry.Name, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<DashboardMetrics.McpServerInfo> GetMcpServers()
    {
        lock (_sync)
        {
            return [.. _mcpServers.Values.OrderBy(static entry => entry.Name, StringComparer.Ordinal)];
        }
    }

    private static void AddNewest<T>(List<T> items, T item, int maximum)
    {
        items.Insert(0, item);
        if (items.Count > maximum)
        {
            items.RemoveRange(maximum, items.Count - maximum);
        }
    }

    private static void TrimOldest<TKey, TValue>(
        Dictionary<TKey, TValue> items,
        int maximum,
        Func<TValue, DateTime> timestamp)
        where TKey : notnull
    {
        while (items.Count > maximum)
        {
            var oldest = items.MinBy(pair => timestamp(pair.Value));
            items.Remove(oldest.Key);
        }
    }
}
