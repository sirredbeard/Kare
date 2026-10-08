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
    private readonly Dictionary<ModelUsageKey, ModelUsageAccumulator> _modelUsage = [];
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

            var key = new ModelUsageKey(
                request.ModelId,
                request.ProviderRouteId,
                request.Backend,
                request.Route);
            if (!_modelUsage.TryGetValue(key, out var usage))
            {
                usage = new ModelUsageAccumulator();
                _modelUsage[key] = usage;
            }

            usage.Record(request);
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
            _routingDecisionUrls[url.Id] = url;
            TrimOldest(_routingDecisionUrls, MaxRegistryEntries, static value => value.LastModifiedAt);
        }
    }

    public void RemoveRoutingDecisionUrl(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
        {
            _routingDecisionUrls.Remove(id);
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

    public void RemoveSkill(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            _skills.Remove(name);
        }
    }

    public void RegisterMcpServer(DashboardMetrics.McpServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_sync)
        {
            _mcpServers[server.Name] = server;
            TrimOldest(
                _mcpServers,
                MaxRegistryEntries,
                static value => value.LastCheckedAt ?? DateTime.MinValue);
        }
    }

    public void RemoveMcpServer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            _mcpServers.Remove(name);
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

    public IReadOnlyList<DashboardMetrics.ModelUsage> GetModelUsage()
    {
        lock (_sync)
        {
            return
            [
                .. _modelUsage
                    .Select(static pair => pair.Value.ToMetric(pair.Key))
                    .OrderByDescending(static usage => usage.RequestCount)
                    .ThenBy(static usage => usage.ModelId, StringComparer.Ordinal)
            ];
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
            return [.. _routingDecisionUrls.Values.OrderBy(static entry => entry.Pattern, StringComparer.Ordinal)];
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

    private readonly record struct ModelUsageKey(
        string ModelId,
        string? ProviderRouteId,
        string Backend,
        string Route);

    private sealed class ModelUsageAccumulator
    {
        private long _requests;
        private long _successful;
        private long _billable;
        private long _fallback;
        private long _inputTokens;
        private long _outputTokens;
        private long _pricedRequests;
        private long _pricedInputTokens;
        private long _pricedOutputTokens;
        private long _successfulPricedRequests;
        private long _successfulPricedInputTokens;
        private long _successfulPricedOutputTokens;
        private double _timeToFirstTokenMs;
        private double _totalDurationMs;
        private double _decodeRate;
        private long _decodeRateSamples;
        private DateTime _lastUsedAt;

        public void Record(DashboardMetrics.RequestMetric request)
        {
            _requests++;
            if (request.Succeeded)
            {
                _successful++;
            }

            if (request.IsBillable)
            {
                _billable++;
            }

            if (request.IsFallback)
            {
                _fallback++;
            }

            _inputTokens += request.InputTokens ?? 0;
            _outputTokens += request.OutputTokens ?? 0;
            if (request.InputTokens is { } inputTokens &&
                request.OutputTokens is { } outputTokens)
            {
                _pricedRequests++;
                _pricedInputTokens += inputTokens;
                _pricedOutputTokens += outputTokens;
                if (request.Succeeded)
                {
                    _successfulPricedRequests++;
                    _successfulPricedInputTokens += inputTokens;
                    _successfulPricedOutputTokens += outputTokens;
                }
            }

            _timeToFirstTokenMs += request.TimeToFirstTokenMs;
            _totalDurationMs += request.TotalDurationMs;
            _lastUsedAt = request.Timestamp;

            if (request.DecodeTokensPerSecond is { } decodeRate)
            {
                _decodeRate += decodeRate;
                _decodeRateSamples++;
            }
        }

        public DashboardMetrics.ModelUsage ToMetric(ModelUsageKey key) =>
            new(
                key.ModelId,
                key.ProviderRouteId,
                key.Backend,
                key.Route,
                _requests,
                _successful,
                _requests - _successful,
                _billable,
                _fallback,
                _inputTokens,
                _outputTokens,
                _timeToFirstTokenMs / _requests,
                _totalDurationMs / _requests,
                _decodeRateSamples == 0 ? null : _decodeRate / _decodeRateSamples,
                _lastUsedAt,
                _pricedRequests,
                _pricedInputTokens,
                _pricedOutputTokens,
                _successfulPricedRequests,
                _successfulPricedInputTokens,
                _successfulPricedOutputTokens);
    }
}
