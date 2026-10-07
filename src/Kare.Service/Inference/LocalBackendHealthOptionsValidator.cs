using Microsoft.Extensions.Options;

namespace Kare.Service.Inference;

/// <summary>Validates long-lived local backend recovery settings.</summary>
public sealed class LocalBackendHealthOptionsValidator :
    IValidateOptions<LocalBackendHealthOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalBackendHealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProbeIntervalSeconds is < 10 or > 3600)
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:Health:ProbeIntervalSeconds must be between 10 and 3600.");
        }

        if (options.ConsecutiveRecoverySuccesses is < 1 or > 10)
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:Health:ConsecutiveRecoverySuccesses must be between 1 and 10.");
        }

        if (options.RecoveryAttemptCooldownSeconds is < 60 or > 86400)
        {
            return ValidateOptionsResult.Fail(
                "Kare:Inference:Health:RecoveryAttemptCooldownSeconds must be between 60 and 86400.");
        }

        return ValidateOptionsResult.Success;
    }
}
