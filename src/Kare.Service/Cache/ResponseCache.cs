using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kare.Abstractions;
using Kare.Service.Dashboard;
using Kare.Service.Options;
using Kare.Service.Routing;
using Kare.Service.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Kare.Service.Cache;

/// <summary>
/// Bounded cache for deterministic, non-tool completions. Keys contain hashes, not prompt
/// text. Protected persistence is optional and stores only eligible response text or route IDs.
/// </summary>
public sealed class ResponseCache : IDisposable
{
    public const string RepositoryFingerprintOptionName = "kare.repository.fingerprint";
    public const string ContextFingerprintOptionName = "kare.context.fingerprint";
    public const string RoutePolicyVersionOptionName = "kare.route-policy.version";

    private const string CacheVersion = "kare-response-v1";
    private const string CascadeCacheVersion = "kare-cascade-route-v1";
    private const string ResponseKind = "response";
    private const string CascadeKind = "cascade";
    private readonly ResponseCacheOptions _options;
    private readonly MemoryCache _cache;
    private readonly IDashboardMetricsCollector? _dashboard;
    private readonly IDashboardKnowledgeService? _knowledge;
    private readonly ILogger<ResponseCache>? _logger;
    private readonly Lock _keysSync = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistentCacheEntry> _persistentEntries =
        new(StringComparer.Ordinal);

    /// <summary>Creates the cache.</summary>
    public ResponseCache(
        IOptions<ResponseCacheOptions> options,
        IDashboardMetricsCollector? dashboard = null,
        IDashboardKnowledgeService? knowledge = null,
        ILogger<ResponseCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _dashboard = dashboard;
        _knowledge = knowledge;
        _logger = logger;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _options.MaxEntries,
        });
        LoadPersistentEntries();
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
            RemoveMissingEntry(key);
            return false;
        }

        var descriptor = BuildDescriptor(messages, response);
        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            DateTime.UtcNow,
            CountBytes(response!),
            "application/json",
            Keywords: descriptor.Keywords,
            TaskClass: descriptor.TaskClass));
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
            response.FinishReason == ChatFinishReason.Length ||
            LocalBackendWarningChatClient.IsWarningResponse(response) ||
            CountText(response) > _options.MaxResponseCharacters)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.AddSeconds(_options.EntryLifetimeSeconds);
        _cache.Set(
            key,
            response,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresAt,
                Size = 1,
            });

        lock (_keysSync)
        {
            _keys.Add(key);
            _persistentEntries[key] = new PersistentCacheEntry(
                key,
                ResponseKind,
                response.Text,
                Target: null,
                now,
                expiresAt,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                response.Usage?.TotalTokenCount);
            PersistLocked();
        }

        var descriptor = BuildDescriptor(messages, response);
        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            LastAccessedAt: null,
            CountBytes(response),
            "application/json",
            Keywords: descriptor.Keywords,
            TaskClass: descriptor.TaskClass));
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
            if (!string.IsNullOrEmpty(key))
            {
                RemoveMissingEntry(key);
            }

            return false;
        }

        target = cachedTarget;
        var descriptor = BuildDescriptor(decisionMessages, response: null);
        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            DateTime.UtcNow,
            Encoding.UTF8.GetByteCount(cachedTarget),
            "application/vnd.kare.cascade-route",
            Keywords: descriptor.Keywords,
            TaskClass: descriptor.TaskClass));
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

        var now = DateTime.UtcNow;
        var expiresAt = now.AddSeconds(_options.EntryLifetimeSeconds);
        _cache.Set(
            key,
            target,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresAt,
                Size = 1,
            });

        lock (_keysSync)
        {
            _keys.Add(key);
            _persistentEntries[key] = new PersistentCacheEntry(
                key,
                CascadeKind,
                Text: null,
                target,
                now,
                expiresAt,
                InputTokens: null,
                OutputTokens: null,
                TotalTokens: null);
            PersistLocked();
        }

        var descriptor = BuildDescriptor(decisionMessages, response: null);
        _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
            key,
            DateTime.UtcNow,
            LastAccessedAt: null,
            Encoding.UTF8.GetByteCount(target),
            "application/vnd.kare.cascade-route",
            Keywords: descriptor.Keywords,
            TaskClass: descriptor.TaskClass));
    }

    /// <summary>Removes a cached response by its opaque hash key.</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _cache.Remove(key);
        lock (_keysSync)
        {
            _keys.Remove(key);
            _persistentEntries.Remove(key);
            PersistLocked();
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
            _persistentEntries.Clear();
            PersistLocked();
        }

        foreach (var key in keys)
        {
            _cache.Remove(key);
            _dashboard?.RemoveCacheEntry(key);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();

    private void LoadPersistentEntries()
    {
        if (!_options.Enabled || !_options.PersistenceEnabled)
        {
            return;
        }

        PersistentCacheState? state = null;
        Exception? lastError = null;
        string? loadedPath = null;
        var candidates = new[] { _options.PersistencePath }
            .Concat(Enumerable.Range(1, _options.BackupCount)
                .Select(index => $"{_options.PersistencePath}.bak{index}"));
        foreach (var candidate in candidates.Where(File.Exists))
        {
            try
            {
                var bytes = File.ReadAllBytes(candidate);
                if (bytes.LongLength > _options.MaxPersistentBytes)
                {
                    throw new InvalidDataException(
                        $"Persistent response cache exceeds {_options.MaxPersistentBytes} bytes.");
                }

                state = JsonSerializer.Deserialize(
                    bytes,
                    CacheJsonContext.Default.PersistentCacheState)
                    ?? throw new InvalidDataException("Persistent response cache is empty.");
                loadedPath = candidate;
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                lastError = ex;
                _logger?.LogError(ex, "Failed to load response-cache snapshot {CachePath}.", candidate);
            }
        }

        if (state is null)
        {
            if (lastError is not null)
            {
                throw new InvalidDataException(
                    "No valid response-cache snapshot or backup could be loaded.",
                    lastError);
            }

            return;
        }

        if (!string.Equals(loadedPath, _options.PersistencePath, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "Recovered response cache from backup snapshot {CachePath}.",
                loadedPath);
        }

        var now = DateTime.UtcNow;
        foreach (var entry in state.Entries
            .Where(item => item.ExpiresAt > now)
            .OrderByDescending(static item => item.CreatedAt)
            .Take(_options.MaxEntries))
        {
            object value;
            if (entry.Kind == ResponseKind && entry.Text is not null)
            {
                value = new ChatResponse(new ChatMessage(ChatRole.Assistant, entry.Text))
                {
                    FinishReason = ChatFinishReason.Stop,
                    Usage = new UsageDetails
                    {
                        InputTokenCount = entry.InputTokens,
                        OutputTokenCount = entry.OutputTokens,
                        TotalTokenCount = entry.TotalTokens,
                    },
                };
            }
            else if (entry.Kind == CascadeKind && !string.IsNullOrWhiteSpace(entry.Target))
            {
                value = entry.Target;
            }
            else
            {
                continue;
            }

            _cache.Set(
                entry.Key,
                value,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = entry.ExpiresAt,
                    Size = 1,
                });
            _keys.Add(entry.Key);
            _persistentEntries[entry.Key] = entry;
            var classified = ContentClassifier.Classify(entry.Text ?? entry.Target ?? string.Empty);
            _dashboard?.RecordCacheEntry(new DashboardMetrics.CacheEntry(
                entry.Key,
                entry.CreatedAt,
                LastAccessedAt: null,
                entry.Text is null
                    ? Encoding.UTF8.GetByteCount(entry.Target!)
                    : Encoding.UTF8.GetByteCount(entry.Text),
                entry.Kind == CascadeKind
                    ? "application/vnd.kare.cascade-route"
                    : "application/json",
                Keywords: classified.Keywords,
                TaskClass: classified.TaskClass));
        }

        PersistLocked();
    }

    /// <summary>
    /// Computes a bounded, fixed-vocabulary descriptor for dashboard display from in-memory
    /// request/response text. Never persisted and never includes the original prompt or
    /// response text itself, only normalized keyword/task-class tags from a closed vocabulary.
    /// </summary>
    private static (IReadOnlyList<string> Keywords, string? TaskClass) BuildDescriptor(
        IReadOnlyList<ChatMessage> messages,
        ChatResponse? response)
    {
        var requestText = string.Join(
            "\n",
            messages
                .SelectMany(static message => message.Contents.OfType<TextContent>())
                .Select(static content => content.Text));
        var combined = response is null
            ? requestText
            : requestText + "\n" + response.Text;
        return ContentClassifier.Classify(combined);
    }

    private void RemoveMissingEntry(string key)
    {
        lock (_keysSync)
        {
            var changed = _keys.Remove(key) | _persistentEntries.Remove(key);
            if (changed)
            {
                PersistLocked();
            }
        }

        _dashboard?.RemoveCacheEntry(key);
    }

    private void PersistLocked()
    {
        if (!_options.PersistenceEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var expired in _persistentEntries.Values
            .Where(entry => entry.ExpiresAt <= now)
            .Select(static entry => entry.Key)
            .ToArray())
        {
            _persistentEntries.Remove(expired);
            _keys.Remove(expired);
            _cache.Remove(expired);
            _dashboard?.RemoveCacheEntry(expired);
        }

        while (_persistentEntries.Count > _options.MaxEntries)
        {
            RemoveOldestPersistentEntry();
        }

        byte[] bytes;
        while (true)
        {
            var state = new PersistentCacheState(
                [.. _persistentEntries.Values.OrderByDescending(static entry => entry.CreatedAt)]);
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                state,
                CacheJsonContext.Default.PersistentCacheState);
            if (bytes.LongLength <= _options.MaxPersistentBytes || _persistentEntries.Count == 0)
            {
                break;
            }

            RemoveOldestPersistentEntry();
        }

        ProtectedStateFile.WriteAtomic(
            _options.PersistencePath,
            bytes,
            _options.BackupCount);
    }

    private void RemoveOldestPersistentEntry()
    {
        var oldest = _persistentEntries.Values.MinBy(static entry => entry.CreatedAt)!;
        _persistentEntries.Remove(oldest.Key);
        _keys.Remove(oldest.Key);
        _cache.Remove(oldest.Key);
        _dashboard?.RemoveCacheEntry(oldest.Key);
    }

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
        AppendFingerprint(hash, options, RepositoryFingerprintOptionName);
        AppendFingerprint(hash, options, ContextFingerprintOptionName);
        AppendFingerprint(hash, options, RoutePolicyVersionOptionName);

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
        AppendFingerprint(hash, options, RepositoryFingerprintOptionName);
        AppendFingerprint(hash, options, ContextFingerprintOptionName);
        AppendFingerprint(hash, options, RoutePolicyVersionOptionName);

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

    private static void AppendFingerprint(
        IncrementalHash hash,
        ChatOptions? options,
        string name)
    {
        var value = options?.AdditionalProperties?.TryGetValue(name, out var raw) == true
            ? raw?.ToString()
            : null;
        Append(hash, name);
        Append(hash, value);
    }
}
