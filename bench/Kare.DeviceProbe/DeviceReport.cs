using System.Text.Json.Serialization;

namespace Kare.DeviceProbe;

/// <summary>
/// Everything the probe could establish about the machine it ran on.
/// A null or empty field means the probe could not read it, not that the feature is absent.
/// </summary>
public sealed record DeviceReport
{
    /// <summary>UTC time the probe ran.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Kernel, distribution, and architecture.</summary>
    public required HostInfo Host { get; init; }

    /// <summary>CPU topology and frequency scaling state.</summary>
    public required CpuInfo Cpu { get; init; }

    /// <summary>Memory totals read from /proc/meminfo.</summary>
    public required MemoryInfo Memory { get; init; }

    /// <summary>Qualcomm FastRPC and DSP device nodes.</summary>
    public required IReadOnlyList<DevicePresence> AcceleratorDevices { get; init; }

    /// <summary>QAIRT and QNN shared libraries the probe could resolve.</summary>
    public required IReadOnlyList<LibraryPresence> AcceleratorLibraries { get; init; }

    /// <summary>ONNX Runtime GenAI native load result.</summary>
    public required OnnxRuntimeInfo OnnxRuntime { get; init; }

    /// <summary>Thermal zones and their temperature at probe time.</summary>
    public required IReadOnlyList<ThermalZone> ThermalZones { get; init; }

    /// <summary>Block devices backing the model and data paths.</summary>
    public required IReadOnlyList<StorageMount> Storage { get; init; }
}

/// <summary>Kernel and distribution identity. Contains no hostname or address.</summary>
/// <param name="OsDescription">Operating system description string.</param>
/// <param name="PrettyName">Distribution PRETTY_NAME from /etc/os-release.</param>
/// <param name="KernelVersion">Kernel release.</param>
/// <param name="Architecture">Process architecture.</param>
/// <param name="RuntimeIdentifier">.NET runtime identifier.</param>
/// <param name="DotNetVersion">.NET runtime version.</param>
public sealed record HostInfo(
    string OsDescription,
    string? PrettyName,
    string KernelVersion,
    string Architecture,
    string RuntimeIdentifier,
    string DotNetVersion);

/// <summary>CPU topology and scaling state.</summary>
/// <param name="LogicalCores">Logical processors visible to the runtime.</param>
/// <param name="ModelNames">Distinct CPU part descriptions, one per cluster.</param>
/// <param name="Cores">Per core frequency and governor state.</param>
public sealed record CpuInfo(
    int LogicalCores,
    IReadOnlyList<string> ModelNames,
    IReadOnlyList<CpuCore> Cores);

/// <summary>One logical CPU's scaling state.</summary>
/// <param name="Index">CPU index.</param>
/// <param name="Governor">Active cpufreq governor.</param>
/// <param name="CurrentKhz">Current frequency in kHz.</param>
/// <param name="MaxKhz">Maximum frequency in kHz.</param>
public sealed record CpuCore(int Index, string? Governor, long? CurrentKhz, long? MaxKhz);

/// <summary>Memory totals in kibibytes as reported by the kernel.</summary>
/// <param name="TotalKib">MemTotal.</param>
/// <param name="AvailableKib">MemAvailable.</param>
/// <param name="SwapTotalKib">SwapTotal.</param>
public sealed record MemoryInfo(long? TotalKib, long? AvailableKib, long? SwapTotalKib);

/// <summary>Whether a device node exists and is usable by this process.</summary>
/// <param name="Path">Device path checked.</param>
/// <param name="Exists">True when the node exists.</param>
/// <param name="Readable">True when this process could open it for read.</param>
/// <param name="Detail">Why it is or is not usable.</param>
public sealed record DevicePresence(string Path, bool Exists, bool Readable, string Detail);

/// <summary>Whether a native library could be resolved and loaded.</summary>
/// <param name="Name">Library name as passed to the loader.</param>
/// <param name="Loaded">True when the loader returned a handle.</param>
/// <param name="ResolvedPath">Path the loader used, when it could be determined.</param>
/// <param name="Detail">Loader error text when the load failed.</param>
public sealed record LibraryPresence(string Name, bool Loaded, string? ResolvedPath, string Detail);

/// <summary>ONNX Runtime GenAI native availability.</summary>
/// <param name="GenAiNativeLoaded">True when libonnxruntime-genai loaded.</param>
/// <param name="QnnProviderLibraryPresent">True when the QNN execution provider library resolved.</param>
/// <param name="ModelPath">Model directory the probe was pointed at, if any.</param>
/// <param name="ModelConfigFound">True when genai_config.json was found.</param>
/// <param name="Detail">Load or configuration detail.</param>
public sealed record OnnxRuntimeInfo(
    bool GenAiNativeLoaded,
    bool QnnProviderLibraryPresent,
    string? ModelPath,
    bool ModelConfigFound,
    string Detail);

/// <summary>A kernel thermal zone reading.</summary>
/// <param name="Zone">Zone directory name.</param>
/// <param name="Type">Zone type, such as a cluster or GPU sensor name.</param>
/// <param name="Celsius">Temperature in degrees Celsius.</param>
public sealed record ThermalZone(string Zone, string? Type, double? Celsius);

/// <summary>A filesystem mount Kare may use for models, cache, or the database.</summary>
/// <param name="Path">Path inspected.</param>
/// <param name="Exists">True when the path exists.</param>
/// <param name="DriveFormat">Filesystem type.</param>
/// <param name="TotalBytes">Total size.</param>
/// <param name="AvailableBytes">Free space.</param>
public sealed record StorageMount(
    string Path,
    bool Exists,
    string? DriveFormat,
    long? TotalBytes,
    long? AvailableBytes);

/// <summary>
/// Compile time JSON metadata. Kare avoids reflection based serialization so the probe
/// stays Native AOT viable and can ship as a single file to the board.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeviceReport))]
[JsonSerializable(typeof(BenchmarkReport))]
public sealed partial class ProbeJsonContext : JsonSerializerContext;
