using System.Globalization;
using System.Text.Json;
using Kare.DeviceProbe;
using Kare.Inference.OnnxGenAI;
using Microsoft.Extensions.Logging;

// kare-probe is the gate tool. It runs on the VENTUNO Q and reports what is actually
// installed and what the board actually does under load. It never assumes a capability
// it could not observe, and it writes machine readable output so results are comparable
// across runs and across board images.

var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "probe";
var parsed = ParseArguments(args);

if (parsed.ContainsKey("help") || command is "help" or "--help" or "-h")
{
    PrintUsage();
    return 0;
}

var outputPath = Get("out");

switch (command)
{
    case "probe":
    {
        var report = SystemInspector.Capture(
            Get("model"),
            [.. (Get("storage") ?? "/,/var/lib/kare").Split(',', StringSplitOptions.RemoveEmptyEntries)]);

        var json = JsonSerializer.Serialize(report, ProbeJsonContext.Default.DeviceReport);
        Emit(json, outputPath);
        PrintProbeSummary(report);
        return report.OnnxRuntime.GenAiNativeLoaded ? 0 : 3;
    }

    case "bench":
    {
        var modelPath = Get("model");
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            Console.Error.WriteLine("bench requires --model <path to ONNX Runtime GenAI model directory>.");
            return 2;
        }

        var options = new OnnxGenAiOptions
        {
            ModelPath = modelPath,
            ModelId = Get("id") ?? Path.GetFileName(modelPath.TrimEnd('/')),
            ExecutionProvider = Get("provider"),
        };

        foreach (var pair in (Get("provider-option") ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                Console.Error.WriteLine($"Malformed --provider-option entry: {pair}. Expected key=value.");
                return 2;
            }

            options.ProviderOptions[pair[..separator]] = pair[(separator + 1)..];
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(console => console.SingleLine = true));

        var runner = new BenchmarkRunner(loggerFactory);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var report = await runner.RunAsync(
            options,
            GetInt("prompt-tokens", 1024),
            GetInt("max-output-tokens", 256),
            GetInt("iterations", 10),
            GetInt("warmup", 2),
            cts.Token).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(report, ProbeJsonContext.Default.BenchmarkReport);
        Emit(json, outputPath);
        PrintBenchSummary(report);
        return report.Summary.Failures == 0 ? 0 : 4;
    }

    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
}

string? Get(string name) => parsed.TryGetValue(name, out var value) ? value : null;

int GetInt(string name, int fallback) =>
    int.TryParse(Get(name), CultureInfo.InvariantCulture, out var value) ? value : fallback;

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[key] = args[++i];
        }
        else
        {
            result[key] = "true";
        }
    }

    return result;
}

static void Emit(string json, string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        Console.WriteLine(json);
        return;
    }

    File.WriteAllText(path, json);
    Console.WriteLine($"Wrote {path}");
}

static void PrintProbeSummary(DeviceReport report)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"host        {report.Host.PrettyName ?? report.Host.OsDescription}");
    Console.Error.WriteLine($"kernel      {report.Host.KernelVersion} {report.Host.Architecture}");
    Console.Error.WriteLine($"runtime     {report.Host.DotNetVersion} ({report.Host.RuntimeIdentifier})");
    Console.Error.WriteLine($"cpus        {report.Cpu.LogicalCores} [{string.Join(", ", report.Cpu.ModelNames)}]");
    Console.Error.WriteLine($"memory      total {Mib(report.Memory.TotalKib)} available {Mib(report.Memory.AvailableKib)}");

    var devices = report.AcceleratorDevices.Where(static d => d.Exists).ToList();
    Console.Error.WriteLine(devices.Count == 0
        ? "dsp         no FastRPC or DSP device nodes found"
        : $"dsp         {string.Join(", ", devices.Select(d => $"{d.Path}{(d.Readable ? string.Empty : " (no permission)")}"))}");

    var libraries = report.AcceleratorLibraries.Where(static l => l.Loaded).ToList();
    Console.Error.WriteLine(libraries.Count == 0
        ? "qairt       no QNN or QAIRT libraries resolved"
        : $"qairt       {string.Join(", ", libraries.Select(static l => l.Name))}");

    Console.Error.WriteLine($"onnxgenai   {report.OnnxRuntime.Detail}");

    var hottest = report.ThermalZones
        .Where(static z => z.Celsius is not null)
        .OrderByDescending(static z => z.Celsius)
        .FirstOrDefault();
    Console.Error.WriteLine(hottest is null
        ? "thermal     no thermal zones readable"
        : $"thermal     {report.ThermalZones.Count} zones, hottest {hottest.Type ?? hottest.Zone} at {hottest.Celsius:F1} C");

    static string Mib(long? kib) => kib is null ? "unknown" : $"{kib.Value / 1024} MiB";
}

static void PrintBenchSummary(BenchmarkReport report)
{
    var summary = report.Summary;
    Console.Error.WriteLine();
    Console.Error.WriteLine($"model       {report.Settings.ModelId} via {report.Settings.Backend}");
    Console.Error.WriteLine($"provider    {report.Settings.ExecutionProvider ?? "genai_config.json default"}");
    Console.Error.WriteLine($"prompt      ~{report.Settings.PromptTokensRequested} tokens, max output {report.Settings.MaxOutputTokens}");
    Console.Error.WriteLine($"iterations  {summary.MeasuredIterations} measured, {summary.Failures} failed, {report.Settings.WarmupIterations} warmup");
    Console.Error.WriteLine($"ttft        median {Ms(summary.TimeToFirstTokenMsMedian)} p95 {Ms(summary.TimeToFirstTokenMsP95)}");
    Console.Error.WriteLine($"decode      median {Rate(summary.DecodeTokensPerSecondMedian)}");
    Console.Error.WriteLine($"total       median {Ms(summary.TotalMsMedian)}");
    Console.Error.WriteLine($"thermal     start {Temp(summary.StartCelsius)} peak {Temp(summary.PeakCelsius)} on {summary.PeakZone ?? "unknown"}");

    if (summary.ThrottlingSuspected)
    {
        Console.Error.WriteLine("warning     decode rate fell while temperature rose. Investigate throttling before trusting these numbers.");
    }

    static string Ms(double? value) => value is null ? "unknown" : $"{value.Value:F0} ms";
    static string Rate(double? value) => value is null
        ? "unknown (backend reported no token usage)"
        : $"{value.Value:F2} tok/s";
    static string Temp(double? value) => value is null ? "unknown" : $"{value.Value:F1} C";
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        kare-probe - VENTUNO Q capability and performance gate tool

          kare-probe probe [--model <dir>] [--storage <a,b>] [--out <file.json>]
              Reports OS, CPU, memory, FastRPC and DSP nodes, QAIRT and QNN libraries,
              ONNX Runtime GenAI native load status, thermal zones, and storage.
              Exit 3 when the ONNX Runtime GenAI native library does not load.

          kare-probe bench --model <dir> [--provider qnn] [--provider-option k=v,k=v]
                           [--prompt-tokens 1024] [--max-output-tokens 256]
                           [--iterations 10] [--warmup 2] [--out <file.json>]
              Runs sustained generation and records time to first token, decode rate,
              temperature, and CPU frequency throughout the run.
              Exit 4 when any measured iteration failed.
        """);
}
