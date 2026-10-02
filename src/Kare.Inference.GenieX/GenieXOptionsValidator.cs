using Microsoft.Extensions.Options;

namespace Kare.Inference.GenieX;

/// <summary>Rejects unsafe or malformed GenieX configuration.</summary>
public sealed class GenieXOptionsValidator : IValidateOptions<GenieXOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GenieXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!options.Endpoint.IsLoopback)
        {
            return ValidateOptionsResult.Fail("GenieX endpoint must use a loopback address.");
        }

        if (!string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail("GenieX endpoint must use HTTP or HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            return ValidateOptionsResult.Fail("GenieX model id is required when GenieX is enabled.");
        }

        if (options.Priority is < 0 or > 1000)
        {
            return ValidateOptionsResult.Fail("GenieX priority must be between 0 and 1000.");
        }

        if (options.ProbeTimeoutSeconds is < 1 or > 60)
        {
            return ValidateOptionsResult.Fail("GenieX probe timeout must be between 1 and 60 seconds.");
        }

        if (options.ModelId.Contains(':', StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "GenieX v0.7.1 rejects precision suffixes in request model ids. Configure the base model id.");
        }

        return ValidateOptionsResult.Success;
    }
}
