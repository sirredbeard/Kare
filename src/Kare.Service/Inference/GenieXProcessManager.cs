using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Kare.Abstractions;
using Kare.Inference.GenieX;
using Microsoft.Extensions.Options;

namespace Kare.Service.Inference;

/// <summary>Owns the GenieX child process and its bounded recycle lifecycle.</summary>
public sealed class GenieXProcessManager :
    IHostedService,
    ILocalBackendRecovery,
    IDisposable
{
    private const int SigTerm = 15;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly GenieXProcessOptions _options;
    private readonly GenieXOptions _backendOptions;
    private readonly ILogger<GenieXProcessManager> _logger;
    private Process? _process;
    private bool _disposed;

    public GenieXProcessManager(
        IOptions<GenieXProcessOptions> options,
        IOptions<GenieXOptions> backendOptions,
        ILogger<GenieXProcessManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backendOptions);
        _options = options.Value;
        _backendOptions = backendOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool Supports(BackendKind backend) =>
        _options.Enabled && backend == BackendKind.GenieXQairt;

    public Task StartAsync(CancellationToken cancellationToken) =>
        EnsureStartedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        StopOwnedProcessAsync(cancellationToken);

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false })
            {
                return;
            }

            _process?.Dispose();
            _process = StartProcess();
            await WaitUntilReadyAsync(_process, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Kare started the owned GenieX process with compute target {Compute}.",
                _options.Compute);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> TryRecoverAsync(
        BackendKind backend,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(backend))
        {
            return false;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _logger.LogWarning(
                    "Kare is recycling its owned GenieX process after an unhealthy NPU probe.");
                await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.RecoverySettleSeconds),
                    cancellationToken).ConfigureAwait(false);
                _process = StartProcess();
                await WaitUntilReadyAsync(_process, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("The owned GenieX process did not become ready before its recovery timeout.");
                await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "The owned GenieX process failed during recovery.");
                await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _process?.Dispose();
        _lifecycleGate.Dispose();
    }

    private Process StartProcess()
    {
        var startInfo = new ProcessStartInfo(_options.ExecutablePath)
        {
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--skip-update");
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add($"{_backendOptions.Endpoint.Host}:{_backendOptions.Endpoint.Port}");
        startInfo.ArgumentList.Add("--nctx");
        startInfo.ArgumentList.Add(_options.ContextTokens.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--compute");
        startInfo.ArgumentList.Add(_options.Compute);
        startInfo.ArgumentList.Add("--power-mode");
        startInfo.ArgumentList.Add(_options.PowerMode);
        startInfo.Environment["GENIEX_DATADIR"] = _options.DataDirectory;
        startInfo.Environment["LD_LIBRARY_PATH"] = _options.NativeLibraryPath;

        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Kare could not start the configured GenieX process.");
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        return process;
    }

    private async Task WaitUntilReadyAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var endpoint = new Uri(
            $"{_backendOptions.Endpoint.ToString().TrimEnd('/')}/models");

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The owned GenieX process exited with code {process.ExitCode} before becoming ready.");
            }

            try
            {
                using var response = await client.GetAsync(endpoint, timeout.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task StopOwnedProcessAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopProcessCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsLinux() && Kill(process.Id, SigTerm) != 0)
            {
                _logger.LogWarning(
                    "Kare could not send SIGTERM to GenieX process {ProcessId}; forcing shutdown.",
                    process.Id);
                process.Kill(entireProcessTree: true);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.StopTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "GenieX did not stop within {TimeoutSeconds} seconds; forcing shutdown.",
                    _options.StopTimeoutSeconds);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
}
