using System.Security.Cryptography;
using System.Text;
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
    private readonly ResponseCacheOptions _options;
    private readonly MemoryCache _cache;
    private readonly IDashboardMetricsCollector? _dashboard;

    /// <summary>Creates the cache.</summary>
    public ResponseCache(
        IOptions<ResponseCacheOptions> options,
        IDashboardMetricsCollector? dashboard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _dashboard = dashboard;
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

        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            LastAccessedAt: null,
            CountBytes(response),
            "application/json"));
    }

    /// <summary>Removes a cached response by its opaque hash key.</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _cache.Remove(key);
        _dashboard?.RemoveCacheEntry(key);
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
