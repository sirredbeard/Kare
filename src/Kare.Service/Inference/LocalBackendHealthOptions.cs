using System.ComponentModel.DataAnnotations;

namespace Kare.Service.Inference;

/// <summary>Long-lived accelerator health and recovery limits.</summary>
public sealed class LocalBackendHealthOptions
{
    public const string SectionName = "Kare:Inference:Health";

    public bool Enabled { get; set; } = true;

    [Range(10, 3600)]
    public int ProbeIntervalSeconds { get; set; } = 300;

    [Range(1, 10)]
    public int ConsecutiveRecoverySuccesses { get; set; } = 2;

    public bool SystemdRecoveryEnabled { get; set; } = true;

    [Range(60, 86400)]
    public int RecoveryAttemptCooldownSeconds { get; set; } = 3600;

    [Range(1, 60)]
    public int StopSettleSeconds { get; set; } = 10;

    [Range(1, 60)]
    public int StartSettleSeconds { get; set; } = 10;

    [Range(1, 120)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}
