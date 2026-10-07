using Kare.Abstractions;
using Microsoft.Extensions.Options;

namespace Kare.Service.Inference;

/// <summary>Probes the preferred accelerator and performs one bounded recovery attempt.</summary>
public sealed class LocalBackendHealthMonitor : BackgroundService
{
    private readonly SelectedBackend _selected;
    private readonly ILocalBackendRecovery _recovery;
    private readonly LocalBackendHealthOptions _options;
    private readonly ILogger<LocalBackendHealthMonitor> _logger;
    private readonly object _signalSync = new();
    private readonly SemaphoreSlim _checkRequested = new(0, 1);
    private DateTimeOffset _lastRecoveryAttemptUtc = DateTimeOffset.MinValue;
    private int _consecutiveSuccesses;
    private int _forceRecovery;

    public LocalBackendHealthMonitor(
        SelectedBackend selected,
        ILocalBackendRecovery recovery,
        IOptions<LocalBackendHealthOptions> options,
        ILogger<LocalBackendHealthMonitor> logger)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RequestCheck()
    {
        lock (_signalSync)
        {
            if (_checkRequested.CurrentCount == 0)
            {
                _checkRequested.Release();
            }
        }
    }

    public void RequestRecovery()
    {
        Interlocked.Exchange(ref _forceRecovery, 1);
        RequestCheck();
    }

    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var preferred = _selected.PreferredBackend;
        var forceRecovery = Interlocked.Exchange(ref _forceRecovery, 0) == 1;
        var result = forceRecovery
            ? BackendProbeResult.Unavailable(
                "A cancelled local inference may still be occupying the GenieX process.")
            : await preferred.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsAvailable)
        {
            _consecutiveSuccesses++;
            if (_selected.IsDegraded &&
                _consecutiveSuccesses >= _options.ConsecutiveRecoverySuccesses)
            {
                _selected.MarkRecovered();
                _logger.LogInformation(
                    "Local accelerator {Backend} recovered after {Count} successful probes.",
                    preferred.Kind,
                    _consecutiveSuccesses);
            }

            return;
        }

        _consecutiveSuccesses = 0;
        var reason = $"{preferred.Kind} health probe failed: {result.Detail}";
        var hasCpuFallback = _selected.MarkDegraded(reason);
        _logger.LogWarning(
            "Local accelerator {Backend} is unhealthy. CPU fallback available: {HasCpuFallback}. {Detail}",
            preferred.Kind,
            hasCpuFallback,
            result.Detail);

        var now = DateTimeOffset.UtcNow;
        if (!_recovery.Supports(preferred.Kind) ||
            now - _lastRecoveryAttemptUtc <
                TimeSpan.FromSeconds(_options.RecoveryAttemptCooldownSeconds))
        {
            return;
        }

        _lastRecoveryAttemptUtc = now;
        if (!await _recovery.TryRecoverAsync(preferred.Kind, cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        var recovered = await preferred.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!recovered.IsAvailable)
        {
            _logger.LogWarning(
                "Local accelerator {Backend} remained unhealthy after its bounded recovery attempt. {Detail}",
                preferred.Kind,
                recovered.Detail);
            return;
        }

        _consecutiveSuccesses = 1;
        if (_options.ConsecutiveRecoverySuccesses == 1)
        {
            _selected.MarkRecovered();
            _logger.LogInformation(
                "Local accelerator {Backend} recovered after the sidecar recycle.",
                preferred.Kind);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.ProbeIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _checkRequested.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
