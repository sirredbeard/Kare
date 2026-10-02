using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kare.Abstractions;
using Kare.Inference.OnnxGenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Kare.DeviceProbe;

/// <summary>
/// Runs sustained generation against a local backend and records latency, throughput,
/// temperature, and frequency. This produces the evidence the plan requires before any
/// performance number about the board is treated as real.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the runner.</summary>
    public BenchmarkRunner(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
    }

    /// <summary>Runs the benchmark and returns the full report.</summary>
    /// <param name="options">Backend configuration.</param>
    /// <param name="promptTokens">Approximate prompt size to build.</param>
    /// <param name="maxOutputTokens">Output cap per iteration.</param>
    /// <param name="iterations">Measured iterations.</param>
    /// <param name="warmup">Warmup iterations excluded from the summary.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<BenchmarkReport> RunAsync(
        OnnxGenAiOptions options,
        int promptTokens,
        int maxOutputTokens,
        int iterations,
        int warmup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var device = SystemInspector.Capture(options.ModelPath, [options.ModelPath]);
        using var backend = new OnnxGenAiBackend(
            Microsoft.Extensions.Options.Options.Create(options),
            _loggerFactory.CreateLogger<OnnxGenAiBackend>());

        var probe = await backend.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsAvailable)
        {
            throw new InvalidOperationException($"Backend is not available: {probe.Detail}");
        }

        var prompt = BuildPrompt(promptTokens);
        var chatOptions = new ChatOptions { MaxOutputTokens = maxOutputTokens };

        var samples = new List<LoadSample>();
        var results = new List<BenchmarkIteration>();
        var startedAt = DateTimeOffset.UtcNow;
        var runClock = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampler = SampleAsync(samples, runClock, samplerCts.Token);

        try
        {
            var total = warmup + iterations;
            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isWarmup = i < warmup;
                results.Add(await RunOneAsync(backend, prompt, chatOptions, i, isWarmup, cancellationToken)
                    .ConfigureAwait(false));
            }
        }
        finally
        {
            await samplerCts.CancelAsync().ConfigureAwait(false);
            await sampler.ConfigureAwait(false);
        }

        return new BenchmarkReport
        {
            StartedAtUtc = startedAt,
            Device = device,
            Settings = new BenchmarkSettings(
                options.ModelId,
                options.ModelPath,
                options.ExecutionProvider,
                backend.Kind.ToString(),
                promptTokens,
                maxOutputTokens,
                iterations,
                warmup),
            Iterations = results,
            Samples = samples,
            Summary = Summarize(results, samples, device),
        };
    }

    private static async Task<BenchmarkIteration> RunOneAsync(
        ILocalInferenceBackend backend,
        string prompt,
        ChatOptions chatOptions,
        int index,
        bool isWarmup,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a terse coding assistant. Answer with code and no preamble."),
            new(ChatRole.User, prompt),
        };

        var text = new StringBuilder();
        long? outputTokens = null;
        var start = Stopwatch.GetTimestamp();
        long? firstToken = null;

        try
        {
            var stream = backend.GetStreamingResponseAsync(messages, chatOptions, cancellationToken);
            await foreach (var update in stream.ConfigureAwait(false))
            {
                firstToken ??= Stopwatch.GetTimestamp();
                text.Append(update.Text);

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usage)
                    {
                        outputTokens = usage.Details.OutputTokenCount;
                    }
                }
            }
        }
        catch (OnnxRuntimeGenAIException ex)
        {
            return Failed(index, isWarmup, prompt.Length, start, firstToken, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(index, isWarmup, prompt.Length, start, firstToken, "Generation timed out.");
        }

        var end = Stopwatch.GetTimestamp();
        var totalMs = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
        var ttftMs = firstToken is { } first
            ? Stopwatch.GetElapsedTime(start, first).TotalMilliseconds
            : totalMs;

        // Fall back to a character based estimate only when the backend reports no usage,
        // and keep the estimate out of the token field so the two are never confused.
        double? decodeRate = null;
        if (outputTokens is > 1 && totalMs > ttftMs)
        {
            decodeRate = (outputTokens.Value - 1) / ((totalMs - ttftMs) / 1000.0);
        }

        return new BenchmarkIteration(
            index,
            isWarmup,
            prompt.Length,
            ttftMs,
            totalMs,
            outputTokens,
            text.Length,
            decodeRate,
            Succeeded: true,
            Detail: null);

        static BenchmarkIteration Failed(
            int index, bool isWarmup, int promptChars, long start, long? firstToken, string detail)
        {
            var totalMs = Stopwatch.GetElapsedTime(start, Stopwatch.GetTimestamp()).TotalMilliseconds;
            var ttftMs = firstToken is { } f ? Stopwatch.GetElapsedTime(start, f).TotalMilliseconds : totalMs;
            return new BenchmarkIteration(
                index, isWarmup, promptChars, ttftMs, totalMs, null, 0, null, false, detail);
        }
    }

    private static async Task SampleAsync(
        List<LoadSample> samples,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var snapshot = SystemInspector.Capture(modelPath: null, extraStoragePaths: []);
                var hottest = snapshot.ThermalZones
                    .Where(static z => z.Celsius is not null)
                    .OrderByDescending(static z => z.Celsius)
                    .FirstOrDefault();

                samples.Add(new LoadSample(
                    clock.Elapsed.TotalMilliseconds,
                    hottest?.Celsius,
                    hottest?.Type ?? hottest?.Zone,
                    snapshot.ThermalZones,
                    [.. snapshot.Cpu.Cores.Select(static c => c.CurrentKhz)],
                    snapshot.Memory.AvailableKib));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected. The sampler runs until the benchmark finishes.
        }
    }

    private static BenchmarkSummary Summarize(
        List<BenchmarkIteration> results,
        List<LoadSample> samples,
        DeviceReport device)
    {
        var measured = results.Where(static r => !r.IsWarmup).ToList();
        var ok = measured.Where(static r => r.Succeeded).ToList();

        var peak = samples
            .Where(static s => s.MaxCelsius is not null)
            .OrderByDescending(static s => s.MaxCelsius)
            .FirstOrDefault();

        var startCelsius = device.ThermalZones
            .Where(static z => z.Celsius is not null)
            .Max(static z => z.Celsius);

        return new BenchmarkSummary(
            ok.Count,
            measured.Count - ok.Count,
            Percentile([.. ok.Select(static r => r.TimeToFirstTokenMs)], 0.50),
            Percentile([.. ok.Select(static r => r.TimeToFirstTokenMs)], 0.95),
            Percentile([.. ok.Where(static r => r.DecodeTokensPerSecond is not null)
                .Select(static r => r.DecodeTokensPerSecond!.Value)], 0.50),
            Percentile([.. ok.Select(static r => r.TotalMs)], 0.50),
            peak?.MaxCelsius,
            peak?.HottestZone,
            startCelsius,
            SuspectThrottling(ok, startCelsius, peak?.MaxCelsius));
    }

    private static bool SuspectThrottling(
        List<BenchmarkIteration> ok,
        double? startCelsius,
        double? peakCelsius)
    {
        // Needs both a meaningful slowdown and a temperature rise. Either one alone is
        // not enough to call throttling, and the probe should not overstate what it saw.
        if (ok.Count < 4 || startCelsius is null || peakCelsius is null)
        {
            return false;
        }

        var rates = ok.Where(static r => r.DecodeTokensPerSecond is not null)
            .Select(static r => r.DecodeTokensPerSecond!.Value)
            .ToList();

        if (rates.Count < 4)
        {
            return false;
        }

        var half = rates.Count / 2;
        var firstHalf = rates.Take(half).Average();
        var lastHalf = rates.Skip(rates.Count - half).Average();

        return firstHalf > 0
            && lastHalf < firstHalf * 0.85
            && peakCelsius.Value - startCelsius.Value > 10.0;
    }

    private static double? Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var rank = (values.Count - 1) * percentile;
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return low == high
            ? values[low]
            : values[low] + ((values[high] - values[low]) * (rank - low));
    }

    private static string BuildPrompt(int approximateTokens)
    {
        // Roughly four characters per token for English source text. The harness reports
        // the real character count and the backend reports real token counts, so this
        // estimate only controls prompt size, never a published measurement.
        const int CharactersPerToken = 4;
        var target = Math.Max(1, approximateTokens) * CharactersPerToken;

        var builder = new StringBuilder(target + 256);
        builder.Append(
            "Review the following C# source for correctness problems and reply with a corrected version.\n\n");

        var counter = 0;
        while (builder.Length < target)
        {
            builder.Append(CultureInfo.InvariantCulture, $"// region {counter}\n");
            builder.Append("public sealed class Widget").Append(counter).Append("\n{\n");
            builder.Append("    private readonly List<int> _items = new();\n");
            builder.Append("    public void Add(int value) { _items.Add(value); }\n");
            builder.Append("    public int Total() { var t = 0; foreach (var i in _items) t += i; return t; }\n");
            builder.Append("}\n\n");
            counter++;
        }

        return builder.ToString();
    }
}
