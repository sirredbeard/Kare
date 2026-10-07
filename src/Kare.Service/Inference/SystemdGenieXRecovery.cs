using System.Diagnostics;
using Kare.Abstractions;
using Microsoft.Extensions.Options;

namespace Kare.Service.Inference;

/// <summary>
/// Gracefully recycles the GenieX user service and leaves device reboot to an operator.
/// </summary>
public sealed class SystemdGenieXRecovery : ILocalBackendRecovery
{
    private const string ServiceName = "kare-geniex.service";
    private readonly LocalBackendHealthOptions _options;
    private readonly ILogger<SystemdGenieXRecovery> _logger;

    public SystemdGenieXRecovery(
        IOptions<LocalBackendHealthOptions> options,
        ILogger<SystemdGenieXRecovery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool Supports(BackendKind backend) =>
        _options.SystemdRecoveryEnabled &&
        backend == BackendKind.GenieXQairt &&
        OperatingSystem.IsLinux();

    public async Task<bool> TryRecoverAsync(
        BackendKind backend,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(backend))
        {
            return false;
        }

        _logger.LogWarning(
            "Stopping {Service} for a bounded FastRPC cleanup interval.",
            ServiceName);

        if (!await RunSystemctlAsync("stop", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await Task.Delay(
            TimeSpan.FromSeconds(_options.StopSettleSeconds),
            cancellationToken).ConfigureAwait(false);

        if (!await RunSystemctlAsync("start", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await Task.Delay(
            TimeSpan.FromSeconds(_options.StartSettleSeconds),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RunSystemctlAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("systemctl")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(ServiceName);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            _logger.LogError("Could not start systemctl for GenieX recovery.");
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "systemctl {Operation} {Service} timed out.",
                operation,
                ServiceName);
            return false;
        }

        if (process.ExitCode == 0)
        {
            return true;
        }

        var error = await process.StandardError.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogError(
            "systemctl {Operation} {Service} failed with exit code {ExitCode}: {Error}",
            operation,
            ServiceName,
            process.ExitCode,
            error.Trim());
        return false;
    }
}
