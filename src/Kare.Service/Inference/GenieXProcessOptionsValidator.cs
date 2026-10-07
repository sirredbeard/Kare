using Microsoft.Extensions.Options;

namespace Kare.Service.Inference;

/// <summary>Validates the fixed GenieX child-process contract.</summary>
public sealed class GenieXProcessOptionsValidator : IValidateOptions<GenieXProcessOptions>
{
    private static readonly string[] AllowedComputeTargets = ["npu", "cpu", "gpu", "hybrid"];

    public ValidateOptionsResult Validate(string? name, GenieXProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!Path.IsPathRooted(options.ExecutablePath) ||
            !File.Exists(options.ExecutablePath))
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:GenieXProcess:ExecutablePath must be an existing absolute file.");
        }

        if (!Path.IsPathRooted(options.WorkingDirectory) ||
            !Directory.Exists(options.WorkingDirectory))
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:GenieXProcess:WorkingDirectory must be an existing absolute directory.");
        }

        if (!Path.IsPathRooted(options.DataDirectory) ||
            !Directory.Exists(options.DataDirectory))
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:GenieXProcess:DataDirectory must be an existing absolute directory.");
        }

        if (!Path.IsPathRooted(options.NativeLibraryPath) ||
            !Directory.Exists(options.NativeLibraryPath))
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:GenieXProcess:NativeLibraryPath must be an existing absolute directory.");
        }

        if (!AllowedComputeTargets.Contains(options.Compute, StringComparer.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:GenieXProcess:Compute must be npu, cpu, gpu, or hybrid.");
        }

        if (string.IsNullOrWhiteSpace(options.PowerMode) ||
            options.PowerMode.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:GenieXProcess:PowerMode contains unsupported characters.");
        }

        return ValidateOptionsResult.Success;
    }
}
