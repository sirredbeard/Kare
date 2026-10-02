using System.Globalization;
using System.Runtime.InteropServices;

namespace Kare.DeviceProbe;

/// <summary>
/// Reads what the kernel reports about this machine.
/// Every read is best effort. A path that cannot be read is recorded as unknown rather
/// than guessed, because the point of the probe is to replace assumptions with facts.
/// </summary>
public static class SystemInspector
{
    /// <summary>
    /// Qualcomm FastRPC and DSP device nodes. Presence of a CDSP node is the cheapest
    /// signal that the Hexagon path is reachable from user space.
    /// </summary>
    private static readonly string[] AcceleratorDevicePaths =
    [
        "/dev/fastrpc-cdsp",
        "/dev/fastrpc-adsp",
        "/dev/fastrpc-sdsp",
        "/dev/fastrpc-mdsp",
        "/dev/adsprpc-smd",
        "/dev/cdsprpc-smd",
        "/dev/ion",
        "/dev/dma_heap/system",
        "/dev/kgsl-3d0",
    ];

    /// <summary>
    /// QAIRT and QNN libraries. These names come from the Qualcomm runtime layout.
    /// A failed load here is the expected result on a board without QAIRT installed.
    /// </summary>
    private static readonly string[] AcceleratorLibraryNames =
    [
        "libQnnHtp.so",
        "libQnnHtpV73Stub.so",
        "libQnnHtpV75Stub.so",
        "libQnnHtpV79Stub.so",
        "libQnnSystem.so",
        "libQnnCpu.so",
        "libQnnGpu.so",
    ];

    /// <summary>Collects a full device report.</summary>
    /// <param name="modelPath">Optional ONNX Runtime GenAI model directory to check.</param>
    /// <param name="extraStoragePaths">Additional paths to report free space for.</param>
    public static DeviceReport Capture(string? modelPath, IReadOnlyList<string> extraStoragePaths)
    {
        return new DeviceReport
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Host = ReadHost(),
            Cpu = ReadCpu(),
            Memory = ReadMemory(),
            AcceleratorDevices = [.. AcceleratorDevicePaths.Select(ReadDevice)],
            AcceleratorLibraries = [.. AcceleratorLibraryNames.Select(ReadLibrary)],
            OnnxRuntime = ReadOnnxRuntime(modelPath),
            ThermalZones = ReadThermalZones(),
            Storage = [.. extraStoragePaths.Select(ReadStorage)],
        };
    }

    private static HostInfo ReadHost() => new(
        RuntimeInformation.OSDescription.Trim(),
        ReadOsReleaseValue("PRETTY_NAME"),
        ReadFirstLine("/proc/sys/kernel/osrelease") ?? "unknown",
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.FrameworkDescription);

    private static string? ReadOsReleaseValue(string key)
    {
        if (!File.Exists("/etc/os-release"))
        {
            return null;
        }

        foreach (var line in File.ReadLines("/etc/os-release"))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..].Trim('"');
            }
        }

        return null;
    }

    private static CpuInfo ReadCpu()
    {
        var modelNames = new List<string>();
        if (File.Exists("/proc/cpuinfo"))
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                // aarch64 reports "CPU part", not "model name". Capture both so the
                // A55 and A78C clusters are distinguishable on this board.
                if (line.StartsWith("model name", StringComparison.Ordinal) ||
                    line.StartsWith("CPU part", StringComparison.Ordinal))
                {
                    var value = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    if (value.Length > 0 && !modelNames.Contains(value))
                    {
                        modelNames.Add(value);
                    }
                }
            }
        }

        var cores = new List<CpuCore>();
        for (var i = 0; i < Environment.ProcessorCount; i++)
        {
            var baseDir = $"/sys/devices/system/cpu/cpu{i}/cpufreq";
            cores.Add(new CpuCore(
                i,
                ReadFirstLine(Path.Combine(baseDir, "scaling_governor")),
                ReadLong(Path.Combine(baseDir, "scaling_cur_freq")),
                ReadLong(Path.Combine(baseDir, "cpuinfo_max_freq"))));
        }

        return new CpuInfo(Environment.ProcessorCount, modelNames, cores);
    }

    private static MemoryInfo ReadMemory()
    {
        long? total = null, available = null, swap = null;
        if (File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMemInfo(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMemInfo(line);
                }
                else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
                {
                    swap = ParseMemInfo(line);
                }
            }
        }

        return new MemoryInfo(total, available, swap);

        static long? ParseMemInfo(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }

    private static DevicePresence ReadDevice(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new DevicePresence(path, false, false, "Not present.");
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new DevicePresence(path, true, true, "Present and openable for read.");
        }
        catch (UnauthorizedAccessException)
        {
            // Common and fixable. The service account needs the device group, which is a
            // deployment step rather than a hardware limitation.
            return new DevicePresence(path, true, false, "Present but permission denied for this user.");
        }
        catch (IOException ex)
        {
            return new DevicePresence(path, true, false, $"Present but not openable: {ex.Message}");
        }
    }

    private static LibraryPresence ReadLibrary(string name)
    {
        if (!NativeLibrary.TryLoad(name, out var handle))
        {
            return new LibraryPresence(name, false, null, "Loader could not resolve the library.");
        }

        try
        {
            return new LibraryPresence(name, true, ResolveLoadedPath(name), "Loaded.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static string? ResolveLoadedPath(string name)
    {
        // /proc/self/maps is the only reliable way to learn where the loader actually
        // found a library, which matters when several QAIRT installs exist on the board.
        if (!File.Exists("/proc/self/maps"))
        {
            return null;
        }

        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            var index = line.IndexOf('/', StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var path = line[index..].Trim();
            if (path.EndsWith(name, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static OnnxRuntimeInfo ReadOnnxRuntime(string? modelPath)
    {
        // Probe through the GenAI assembly so the loader applies the same lib prefix,
        // .so suffix, and assembly directory rules that DllImport would. A plain
        // TryLoad(name) skips those rules and reports a false negative.
        var genAiLoaded = TryLoadLikeDllImport("onnxruntime-genai");

        // The stock CPU package does not ship this. Its absence is the concrete reason a
        // QNN execution provider cannot be used from the packaged runtime.
        var qnnLoaded = TryLoadLikeDllImport("onnxruntime_providers_qnn");

        var configFound = false;
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            configFound = File.Exists(Path.Combine(modelPath, "genai_config.json"));
        }

        var detail = (genAiLoaded, qnnLoaded) switch
        {
            (false, _) => "libonnxruntime-genai did not load. The CPU path is unavailable.",
            (true, false) => "GenAI native loaded. No QNN provider library, so only the config declared provider is usable.",
            (true, true) => "GenAI native and QNN provider library both loaded.",
        };

        return new OnnxRuntimeInfo(genAiLoaded, qnnLoaded, modelPath, configFound, detail);
    }

    private static bool TryLoadLikeDllImport(string libraryName)
    {
        var assembly = typeof(Microsoft.ML.OnnxRuntimeGenAI.Config).Assembly;
        var searchPath = DllImportSearchPath.AssemblyDirectory
            | DllImportSearchPath.ApplicationDirectory
            | DllImportSearchPath.SafeDirectories
            | DllImportSearchPath.System32;

        if (!NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    private static List<ThermalZone> ReadThermalZones()
    {
        var zones = new List<ThermalZone>();
        if (!Directory.Exists("/sys/class/thermal"))
        {
            return zones;
        }

        foreach (var dir in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*").Order(StringComparer.Ordinal))
        {
            var milli = ReadLong(Path.Combine(dir, "temp"));
            zones.Add(new ThermalZone(
                Path.GetFileName(dir),
                ReadFirstLine(Path.Combine(dir, "type")),
                milli / 1000.0));
        }

        return zones;
    }

    private static StorageMount ReadStorage(string path)
    {
        if (!Directory.Exists(path))
        {
            return new StorageMount(path, false, null, null, null);
        }

        var drive = new DriveInfo(path);
        return new StorageMount(path, true, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace);
    }

    private static string? ReadFirstLine(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadLines(path).FirstOrDefault()?.Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadLong(string path) =>
        long.TryParse(ReadFirstLine(path), CultureInfo.InvariantCulture, out var value) ? value : null;
}
