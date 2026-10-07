using System.ComponentModel;
using System.Diagnostics;
using System.Net;

namespace Kare.CopilotLauncher;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        return new CopilotKareApp().RunAsync(args);
    }
}

internal sealed class CopilotKareApp
{
    private const string ConfigDirectoryName = "kare";
    private const string LastDeviceHostFileName = "last-device-host";
    private const string DefaultLocalPort = "5285";
    private const string DefaultRemotePort = "5285";
    private const int DefaultHealthTimeoutSeconds = 20;

    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await RunCoreAsync(args);
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Kare could not access a required file or directory: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Kare could not read or write its local configuration: {exception.Message}");
            return 1;
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"Kare could not start a required command: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Kare could not start GitHub Copilot: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        var input = LauncherInputParser.Parse(args);
        if (input.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var configFile = GetConfigFilePath();
        var configValues = ConfigFile.Load(configFile);
        var configDirectory = Path.GetDirectoryName(configFile) ?? GetDefaultConfigDirectory();
        var lastDeviceHostFile = Path.Combine(configDirectory, LastDeviceHostFileName);
        var deviceHost = DeviceHostResolver.Resolve(input.DeviceHost, configValues, lastDeviceHostFile);

        if (string.IsNullOrWhiteSpace(deviceHost))
        {
            Console.Error.WriteLine("A device IP or host name is required.");
            PrintUsage();
            return 2;
        }

        var deviceUser = GetSetting(configValues, "KARE_DEVICE_USER", Environment.UserName);
        var apiKey = GetSetting(configValues, "KARE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("KARE_API_KEY is required. Set it in the shell or protected device config.");
            return 1;
        }

        var devicePassword = GetSetting(configValues, "KARE_DEVICE_PASS");
        var localPort = GetPortSetting(configValues, "KARE_TUNNEL_LOCAL_PORT", DefaultLocalPort);
        var remotePort = GetPortSetting(configValues, "KARE_TUNNEL_REMOTE_PORT", DefaultRemotePort);
        var healthTimeoutSeconds = GetPositiveIntegerSetting(
            configValues,
            "KARE_TUNNEL_READY_TIMEOUT",
            DefaultHealthTimeoutSeconds);

        if (localPort is null || remotePort is null || healthTimeoutSeconds is null)
        {
            return 1;
        }

        using var sshProcess = CreateSshProcess(
            deviceUser,
            deviceHost,
            localPort,
            remotePort,
            devicePassword);

        sshProcess.Start();
        var sshErrorTask = sshProcess.StandardError.ReadToEndAsync();

        try
        {
            var healthBaseUrl = $"http://127.0.0.1:{localPort}";
            var isReady = await WaitForHealthAsync(
                sshProcess,
                sshErrorTask,
                healthBaseUrl,
                TimeSpan.FromSeconds(healthTimeoutSeconds.Value));

            if (!isReady)
            {
                return 1;
            }

            WriteLastDeviceHost(configDirectory, lastDeviceHostFile, deviceHost);

            Console.WriteLine($"Kare is ready at {healthBaseUrl}. Starting GitHub Copilot with the BYOK provider.");

            using var copilotProcess = StartCopilot(input.CopilotArguments, configValues, healthBaseUrl);
            await copilotProcess.WaitForExitAsync();
            return copilotProcess.ExitCode;
        }
        finally
        {
            StopProcess(sshProcess, "SSH tunnel");
        }
    }

    private static string GetConfigFilePath()
    {
        return Environment.GetEnvironmentVariable("KARE_DEVICE_CONFIG") ??
            Path.Combine(GetDefaultConfigDirectory(), "device.env");
    }

    private static string GetDefaultConfigDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            ConfigDirectoryName);
    }

    private static string GetSetting(
        IReadOnlyDictionary<string, string> configValues,
        string key,
        string defaultValue = "")
    {
        var environmentValue = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return configValues.TryGetValue(key, out var configValue) && !string.IsNullOrWhiteSpace(configValue)
            ? configValue
            : defaultValue;
    }

    private static int? GetPositiveIntegerSetting(
        IReadOnlyDictionary<string, string> configValues,
        string key,
        int defaultValue)
    {
        var value = GetSetting(configValues, key, defaultValue.ToString());
        if (int.TryParse(value, out var result) && result > 0)
        {
            return result;
        }

        Console.Error.WriteLine($"{key} must be a positive integer.");
        return null;
    }

    private static string? GetPortSetting(
        IReadOnlyDictionary<string, string> configValues,
        string key,
        string defaultValue)
    {
        var value = GetSetting(configValues, key, defaultValue);
        if (int.TryParse(value, out var port) && port is >= 1 and <= 65535)
        {
            return value;
        }

        Console.Error.WriteLine($"{key} must be a port from 1 through 65535.");
        return null;
    }

    private static void WriteLastDeviceHost(string configDirectory, string path, string deviceHost)
    {
        EnsurePrivateDirectory(configDirectory);
        File.WriteAllText(path, deviceHost);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static Process CreateSshProcess(
        string deviceUser,
        string deviceHost,
        string localPort,
        string remotePort,
        string devicePassword)
    {
        var sshArguments = BuildSshArguments(deviceUser, deviceHost, localPort, remotePort);
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(devicePassword) ? "ssh" : "sshpass",
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(devicePassword))
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("ssh");
            startInfo.Environment["SSHPASS"] = devicePassword;
        }

        foreach (var argument in sshArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process
        {
            StartInfo = startInfo,
        };
    }

    private static IReadOnlyList<string> BuildSshArguments(
        string deviceUser,
        string deviceHost,
        string localPort,
        string remotePort)
    {
        return
        [
            "-o",
            "ExitOnForwardFailure=yes",
            "-o",
            "ConnectTimeout=15",
            "-o",
            "ServerAliveInterval=30",
            "-o",
            "ServerAliveCountMax=3",
            "-o",
            "StrictHostKeyChecking=accept-new",
            "-N",
            "-L",
            $"{localPort}:127.0.0.1:{remotePort}",
            $"{deviceUser}@{deviceHost}",
        ];
    }

    private static async Task<bool> WaitForHealthAsync(
        Process sshProcess,
        Task<string> sshErrorTask,
        string baseUrl,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (sshProcess.HasExited)
            {
                var error = (await sshErrorTask).Trim();
                Console.Error.WriteLine("SSH tunnel exited before Kare became ready.");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Console.Error.WriteLine(error);
                }

                return false;
            }

            if (await IsHealthyAsync(baseUrl))
            {
                return true;
            }

            await Task.Delay(500);
        }

        Console.Error.WriteLine($"Kare did not become ready at {baseUrl}/health within {timeout.TotalSeconds:0}s.");
        return false;
    }

    private static async Task<bool> IsHealthyAsync(string baseUrl)
    {
        try
        {
            using var response = await HealthClient.GetAsync($"{baseUrl}/health");
            return response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static Process StartCopilot(
        IReadOnlyList<string> copilotArguments,
        IReadOnlyDictionary<string, string> configValues,
        string healthBaseUrl)
    {
        var copilotHome = GetSetting(
            configValues,
            "KARE_COPILOT_HOME",
            Path.Combine(GetDefaultConfigDirectory(), "copilot-home"));
        EnsurePrivateDirectory(copilotHome);

        var modelId = GetSetting(configValues, "KARE_MODEL_ID", "kare");
        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            UseShellExecute = false,
        };

        startInfo.Environment["COPILOT_PROVIDER_BASE_URL"] = $"{healthBaseUrl}/v1";
        startInfo.Environment["COPILOT_PROVIDER_TYPE"] = "openai";
        startInfo.Environment["COPILOT_PROVIDER_WIRE_API"] = "completions";
        startInfo.Environment["COPILOT_PROVIDER_API_KEY"] = GetSetting(configValues, "KARE_API_KEY");
        startInfo.Environment["COPILOT_PROVIDER_WIRE_MODEL"] = GetSetting(configValues, "KARE_WIRE_MODEL", "kare");
        startInfo.Environment["COPILOT_PROVIDER_MODEL_ID"] = modelId;
        startInfo.Environment["COPILOT_MODEL"] = modelId;
        startInfo.Environment["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] =
            GetSetting(configValues, "KARE_MAX_PROMPT_TOKENS", "7168");
        startInfo.Environment["COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"] =
            GetSetting(configValues, "KARE_MAX_OUTPUT_TOKENS", "1024");
        startInfo.Environment["COPILOT_HOME"] = copilotHome;

        foreach (var argument in copilotArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("GitHub Copilot did not start.");
    }

    private static void StopProcess(Process process, string processName)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"{processName} cleanup failed: {exception.Message}");
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"{processName} cleanup failed: {exception.Message}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: copilot-kare [device-ip-or-host] [copilot args...]");
        Console.WriteLine("       copilot-kare [copilot args...]");
        Console.WriteLine();
        Console.WriteLine("The explicit device address overrides environment and config values.");
        Console.WriteLine("Without one, the launcher uses KARE_DEVICE_HOST, protected config, or the last working address.");
    }
}

internal sealed record LauncherInput(string? DeviceHost, string[] CopilotArguments, bool ShowHelp);

internal static class LauncherInputParser
{
    public static LauncherInput Parse(string[] args)
    {
        if (args.Length > 0 && IsHelpArgument(args[0]))
        {
            return new LauncherInput(null, [], true);
        }

        if (args.Length > 0 && !args[0].StartsWith('-', StringComparison.Ordinal))
        {
            return new LauncherInput(args[0], args[1..], false);
        }

        return new LauncherInput(null, args, false);
    }

    private static bool IsHelpArgument(string argument)
    {
        return argument is "--help" or "-h" or "/?" or "-help";
    }
}

internal static class DeviceHostResolver
{
    public static string? Resolve(
        string? explicitDeviceHost,
        IReadOnlyDictionary<string, string> configValues,
        string lastDeviceHostFile)
    {
        if (!string.IsNullOrWhiteSpace(explicitDeviceHost))
        {
            return explicitDeviceHost;
        }

        var environmentHost = Environment.GetEnvironmentVariable("KARE_DEVICE_HOST") ??
            Environment.GetEnvironmentVariable("KARE_DEVICE_IP");
        if (!string.IsNullOrWhiteSpace(environmentHost))
        {
            return environmentHost;
        }

        if (configValues.TryGetValue("KARE_DEVICE_HOST", out var configHost) &&
            !string.IsNullOrWhiteSpace(configHost))
        {
            return configHost;
        }

        if (!File.Exists(lastDeviceHostFile))
        {
            return null;
        }

        var lastDeviceHost = File.ReadAllText(lastDeviceHostFile).Trim();
        return string.IsNullOrWhiteSpace(lastDeviceHost) ? null : lastDeviceHost;
    }
}

internal static class ConfigFile
{
    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new InvalidDataException($"Invalid config entry at {path}:{lineNumber}.");
            }

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
