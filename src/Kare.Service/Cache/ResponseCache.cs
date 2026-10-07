using System.Security.Cryptography;
using System.Text;
using Kare.Abstractions;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Kare.Service.Cache;

/// <summary>
/// Bounded memory-only cache for deterministic, non-tool completions. Keys contain hashes,
/// not prompt text, and entries disappear on process restart.
/// </summary>
public sealed class ResponseCache : IDisposable
{
    private const string CacheVersion = "kare-response-v1";
    private const string CascadeCacheVersion = "kare-cascade-route-v1";
    private readonly ResponseCacheOptions _options;
    private readonly MemoryCache _cache;
    private readonly IDashboardMetricsCollector? _dashboard;
    private readonly IDashboardKnowledgeService? _knowledge;
    private readonly Lock _keysSync = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Creates the cache.</summary>
    public ResponseCache(
        IOptions<ResponseCacheOptions> options,
        IDashboardMetricsCollector? dashboard = null,
        IDashboardKnowledgeService? knowledge = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _dashboard = dashboard;
        _knowledge = knowledge;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _options.MaxEntries,
        });
    }

    /// <summary>Gets an eligible cached response.</summary>
    public bool TryGet(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        bool streaming,
        out ChatResponse? response)
    {
        response = null;
        if (!TryCreateKey(messages, options, streaming, out var key))
        {
            return false;
        }

        if (!_cache.TryGetValue(key, out response))
        {
            lock (_keysSync)
            {
                _keys.Remove(key);
            }

            _dashboard?.RemoveCacheEntry(key);
            return false;
        }

        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            DateTime.UtcNow,
            CountBytes(response!),
            "application/json"));
        return true;
    }

    /// <summary>Stores an eligible response within configured bounds.</summary>
    public void Set(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        bool streaming,
        ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!TryCreateKey(messages, options, streaming, out var key) ||
            CountText(response) > _options.MaxResponseCharacters)
        {
            return;
        }

        _cache.Set(
            key,
            response,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow =
                    TimeSpan.FromSeconds(_options.EntryLifetimeSeconds),
                Size = 1,
            });

        lock (_keysSync)
        {
            _keys.Add(key);
        }

        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            LastAccessedAt: null,
            CountBytes(response),
            "application/json"));
    }

    /// <summary>Gets a cached cloud target selected by the local cascade gate.</summary>
    public bool TryGetCascadeTarget(
        IReadOnlyList<ChatMessage> decisionMessages,
        ChatOptions? options,
        IReadOnlyList<CloudModelDescriptor> candidates,
        out string? target)
    {
        target = null;
        if (!TryCreateCascadeKey(decisionMessages, options, candidates, out var key) ||
            !_cache.TryGetValue(key, out string? cachedTarget) ||
            string.IsNullOrWhiteSpace(cachedTarget))
        {
            return false;
        }

        target = cachedTarget;
        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            DateTime.UtcNow,
            Encoding.UTF8.GetByteCount(cachedTarget),
            "application/vnd.kare.cascade-route"));
        return true;
    }

    /// <summary>Caches only the selected cloud target, never prompt or response text.</summary>
    public void SetCascadeTarget(
        IReadOnlyList<ChatMessage> decisionMessages,
        ChatOptions? options,
        IReadOnlyList<CloudModelDescriptor> candidates,
        string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!TryCreateCascadeKey(decisionMessages, options, candidates, out var key))
        {
            return;
        }

        _cache.Set(
            key,
            target,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow =
                    TimeSpan.FromSeconds(_options.EntryLifetimeSeconds),
                Size = 1,
            });

        lock (_keysSync)
        {
            _keys.Add(key);
        }

        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            LastAccessedAt: null,
            Encoding.UTF8.GetByteCount(target),
            "application/vnd.kare.cascade-route"));
    }

    /// <summary>Removes a cached response by its opaque hash key.</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _cache.Remove(key);
        lock (_keysSync)
        {
            _keys.Remove(key);
        }

        _dashboard?.RemoveCacheEntry(key);
    }

    /// <summary>Removes every response currently tracked by the bounded cache.</summary>
    public void Clear()
    {
        string[] keys;
        lock (_keysSync)
        {
            keys = [.. _keys];
            _keys.Clear();
        }

        foreach (var key in keys)
        {
            _cache.Remove(key);
            _dashboard?.RemoveCacheEntry(key);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();

    private bool TryCreateKey(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        bool streaming,
        out string key)
    {
        key = string.Empty;
        if (!_options.Enabled ||
            streaming ||
            options.Tools is { Count: > 0 } ||
            options.Temperature is > 0)
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, CacheVersion);
        Append(hash, _knowledge?.ContextVersion);
        Append(hash, options.ModelId);
        Append(hash, options.Temperature);
        Append(hash, options.TopP);
        Append(hash, options.Seed);
        Append(hash, options.MaxOutputTokens);

        foreach (var stop in options.StopSequences ?? [])
        {
            Append(hash, stop);
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                return false;
            }

            Append(hash, message.Role.Value);
            foreach (var content in message.Contents)
            {
                if (content is not TextContent text)
                {
                    return false;
                }

                Append(hash, text.Text);
            }
        }

        key = Convert.ToHexString(hash.GetHashAndReset());
        return true;
    }

    private bool TryCreateCascadeKey(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<CloudModelDescriptor> candidates,
        out string key)
    {
        key = string.Empty;
        if (!_options.Enabled || candidates.Count == 0)
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, CascadeCacheVersion);
        Append(hash, _knowledge?.ContextVersion);
        Append(hash, options?.MaxOutputTokens);
        Append(hash, options?.ToolMode);

        foreach (var candidate in candidates)
        {
            Append(hash, candidate.Id);
            Append(hash, candidate.ModelId);
            Append(hash, candidate.Priority);
            Append(hash, candidate.SupportsTools);
            Append(hash, candidate.SupportsImages);
        }

        foreach (var tool in options?.Tools?.OfType<AIFunctionDeclaration>() ?? [])
        {
            Append(hash, tool.Name);
            Append(hash, tool.Description);
        }

        foreach (var message in messages)
        {
            Append(hash, message.Role.Value);
            foreach (var content in message.Contents)
            {
                Append(hash, content is TextContent text ? text.Text : content.GetType().FullName);
            }
        }

        key = Convert.ToHexString(hash.GetHashAndReset());
        return true;
    }

    private static int CountText(ChatResponse response)
    {
        var count = 0;
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    count += text.Text.Length;
                }
            }
        }

        return count;
    }

    private static long CountBytes(ChatResponse response)
    {
        long count = 0;
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    count += Encoding.UTF8.GetByteCount(text.Text);
                }
            }
        }

        return count;
    }

    private static void Append<T>(IncrementalHash hash, T value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
        var bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
