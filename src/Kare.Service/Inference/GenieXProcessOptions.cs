using System.ComponentModel.DataAnnotations;

namespace Kare.Service.Inference;

/// <summary>Protected settings for the GenieX child process owned by Kare.</summary>
public sealed class GenieXProcessOptions
{
    public const string SectionName = "Kare:Inference:GenieXProcess";

    public bool Enabled { get; set; }

    public string ExecutablePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string DataDirectory { get; set; } = string.Empty;

    [Range(1024, 131072)]
    public int ContextTokens { get; set; } = 8192;

    public string Compute { get; set; } = "npu";

    public string PowerMode { get; set; } = "sustained_high_performance";

    [Range(5, 180)]
    public int StartupTimeoutSeconds { get; set; } = 60;

    [Range(1, 60)]
    public int StopTimeoutSeconds { get; set; } = 20;

    [Range(1, 60)]
    public int RecoverySettleSeconds { get; set; } = 10;
}
