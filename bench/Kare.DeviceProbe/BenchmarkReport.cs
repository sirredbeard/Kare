namespace Kare.DeviceProbe;

/// <summary>
/// Result of a sustained generation run. This is the artifact the plan requires before
/// any performance claim about the VENTUNO Q is written down.
/// </summary>
public sealed record BenchmarkReport
{
    /// <summary>UTC time the run started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>The device state captured immediately before the run.</summary>
    public required DeviceReport Device { get; init; }

    /// <summary>What was run.</summary>
    public required BenchmarkSettings Settings { get; init; }

    /// <summary>One entry per iteration, in order.</summary>
    public required IReadOnlyList<BenchmarkIteration> Iterations { get; init; }

    /// <summary>Thermal and frequency samples taken while generating.</summary>
    public required IReadOnlyList<LoadSample> Samples { get; init; }

    /// <summary>Aggregates across all successful iterations.</summary>
    public required BenchmarkSummary Summary { get; init; }
}

/// <summary>What the benchmark was asked to do.</summary>
/// <param name="ModelId">Model label.</param>
/// <param name="ModelPath">Model directory.</param>
/// <param name="ExecutionProvider">Provider appended, or null for the config default.</param>
/// <param name="Backend">Backend kind the run reported.</param>
/// <param name="PromptTokensRequested">Approximate prompt size the harness targeted.</param>
/// <param name="MaxOutputTokens">Output cap per iteration.</param>
/// <param name="Iterations">Iteration count.</param>
/// <param name="WarmupIterations">Warmup iterations excluded from the summary.</param>
public sealed record BenchmarkSettings(
    string ModelId,
    string ModelPath,
    string? ExecutionProvider,
    string Backend,
    int PromptTokensRequested,
    int MaxOutputTokens,
    int Iterations,
    int WarmupIterations);

/// <summary>One generation.</summary>
/// <param name="Index">Zero based iteration number.</param>
/// <param name="IsWarmup">True when excluded from the summary.</param>
/// <param name="PromptCharacters">Characters sent.</param>
/// <param name="TimeToFirstTokenMs">Prompt processing plus first decode step.</param>
/// <param name="TotalMs">Full generation wall clock.</param>
/// <param name="OutputTokens">Tokens reported by the backend, when available.</param>
/// <param name="OutputCharacters">Characters generated.</param>
/// <param name="DecodeTokensPerSecond">Steady state decode rate, when derivable.</param>
/// <param name="Succeeded">False when the iteration failed or timed out.</param>
/// <param name="Detail">Failure detail, when it failed.</param>
public sealed record BenchmarkIteration(
    int Index,
    bool IsWarmup,
    int PromptCharacters,
    double TimeToFirstTokenMs,
    double TotalMs,
    long? OutputTokens,
    int OutputCharacters,
    double? DecodeTokensPerSecond,
    bool Succeeded,
    string? Detail);

/// <summary>A thermal and frequency sample taken during the run.</summary>
/// <param name="ElapsedMs">Milliseconds since the run started.</param>
/// <param name="MaxCelsius">Hottest thermal zone at this moment.</param>
/// <param name="HottestZone">Which zone was hottest.</param>
/// <param name="Zones">Every zone reading.</param>
/// <param name="CpuKhz">Per core current frequency.</param>
/// <param name="AvailableMemoryKib">MemAvailable at this moment.</param>
public sealed record LoadSample(
    double ElapsedMs,
    double? MaxCelsius,
    string? HottestZone,
    IReadOnlyList<ThermalZone> Zones,
    IReadOnlyList<long?> CpuKhz,
    long? AvailableMemoryKib);

/// <summary>Aggregates over the measured iterations.</summary>
/// <param name="MeasuredIterations">Iterations counted, excluding warmup.</param>
/// <param name="Failures">Iterations that did not complete.</param>
/// <param name="TimeToFirstTokenMsMedian">Median time to first token.</param>
/// <param name="TimeToFirstTokenMsP95">95th percentile time to first token.</param>
/// <param name="DecodeTokensPerSecondMedian">Median steady state decode rate.</param>
/// <param name="TotalMsMedian">Median total generation time.</param>
/// <param name="PeakCelsius">Hottest reading observed during the run.</param>
/// <param name="PeakZone">Zone that produced the hottest reading.</param>
/// <param name="StartCelsius">Hottest reading before the run began.</param>
/// <param name="ThrottlingSuspected">
/// True when decode rate degraded materially from the first measured iteration to the last
/// while temperature rose. This is a signal to investigate, not a confirmed diagnosis.
/// </param>
public sealed record BenchmarkSummary(
    int MeasuredIterations,
    int Failures,
    double? TimeToFirstTokenMsMedian,
    double? TimeToFirstTokenMsP95,
    double? DecodeTokensPerSecondMedian,
    double? TotalMsMedian,
    double? PeakCelsius,
    string? PeakZone,
    double? StartCelsius,
    bool ThrottlingSuspected);
