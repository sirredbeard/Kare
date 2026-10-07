using System.ComponentModel.DataAnnotations;

namespace Kare.Service.Options;

/// <summary>Privacy-first bounded response cache settings.</summary>
public sealed class ResponseCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Cache:Responses";

    /// <summary>Enables the in-memory response cache.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum number of cached responses.</summary>
    [Range(1, 100_000)]
    public int MaxEntries { get; set; } = 256;

    /// <summary>Absolute lifetime of one cache entry.</summary>
    [Range(1, 86_400)]
    public int EntryLifetimeSeconds { get; set; } = 900;

    /// <summary>Maximum response text stored in one entry.</summary>
    [Range(1_024, 10_000_000)]
    public int MaxResponseCharacters { get; set; } = 64_000;
}
