using System.Text.Json.Serialization;

namespace Kare.Service.Cache;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(PersistentCacheState))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;

internal sealed record PersistentCacheState(List<PersistentCacheEntry> Entries);

internal sealed record PersistentCacheEntry(
    string Key,
    string Kind,
    string? Text,
    string? Target,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens);
