using Microsoft.Extensions.Options;

namespace Kare.Cloud.Copilot;

/// <summary>Validates conditional cloud configuration without reading secret values.</summary>
public sealed class CopilotSdkOptionsValidator : IValidateOptions<CopilotSdkOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CopilotSdkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            return ValidateOptionsResult.Fail("ModelId is required when Copilot SDK escalation is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.StateDirectory) ||
            !Path.IsPathRooted(options.StateDirectory))
        {
            return ValidateOptionsResult.Fail("StateDirectory must be an absolute path.");
        }

        if (options.Provider == CopilotCloudProvider.MicrosoftFoundry &&
            (!Uri.TryCreate(options.FoundryBaseUrl, UriKind.Absolute, out var endpoint) ||
             endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                "Microsoft Foundry requires an absolute HTTPS FoundryBaseUrl.");
        }

        if (options.Provider == CopilotCloudProvider.MicrosoftFoundry &&
            options.FoundryWireApi is not ("responses" or "anthropic"))
        {
            return ValidateOptionsResult.Fail(
                "FoundryWireApi must be 'responses' or 'anthropic'.");
        }

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || !routeIds.Add(model.Id))
            {
                return ValidateOptionsResult.Fail(
                    "Each cloud model requires a unique non-empty Id.");
            }

            if (string.IsNullOrWhiteSpace(model.ModelId))
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} requires ModelId.");
            }

            var hasInputPrice = model.AverageInputCostUsdPerMillionTokens is not null;
            var hasOutputPrice = model.AverageOutputCostUsdPerMillionTokens is not null;
            if (hasInputPrice != hasOutputPrice)
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} must configure both average input and output prices.");
            }

            if ((model.AverageInputCostUsdPerMillionTokens is < 0m ||
                 model.AverageOutputCostUsdPerMillionTokens is < 0m))
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} average prices cannot be negative.");
            }

            if (hasInputPrice &&
                (string.IsNullOrWhiteSpace(model.PricingSource) || model.PricingAsOf is null))
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} requires PricingSource and PricingAsOf when prices are configured.");
            }

            if (model.Provider != Kare.Abstractions.CloudModelProvider.MicrosoftFoundry)
            {
                if (model.Authentication != CloudAuthentication.Copilot)
                {
                    return ValidateOptionsResult.Fail(
                        $"GitHub Copilot model {model.Id} must use Copilot authentication.");
                }

                continue;
            }

            if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var modelEndpoint) ||
                modelEndpoint.Scheme != Uri.UriSchemeHttps)
            {
                return ValidateOptionsResult.Fail(
                    $"Foundry model {model.Id} requires an absolute HTTPS BaseUrl.");
            }

            if (string.IsNullOrWhiteSpace(model.WireModel))
            {
                return ValidateOptionsResult.Fail(
                    $"Foundry model {model.Id} requires WireModel.");
            }

            if (model.WireApi is not ("responses" or "anthropic"))
            {
                return ValidateOptionsResult.Fail(
                    $"Foundry model {model.Id} WireApi must be 'responses' or 'anthropic'.");
            }

            if (model.Authentication == CloudAuthentication.EnvironmentApiKey &&
                string.IsNullOrWhiteSpace(model.ApiKeyEnvironmentVariable))
            {
                return ValidateOptionsResult.Fail(
                    $"Foundry model {model.Id} requires ApiKeyEnvironmentVariable for environment-key authentication.");
            }

            if (model.Authentication == CloudAuthentication.AzureCli &&
                (!Uri.TryCreate(model.TokenScope, UriKind.Absolute, out var scope) ||
                 scope.Scheme != Uri.UriSchemeHttps))
            {
                return ValidateOptionsResult.Fail(
                    $"Foundry model {model.Id} requires an absolute HTTPS TokenScope for Azure CLI authentication.");
            }

            if (model.Authentication == CloudAuthentication.Copilot)
            {
                return ValidateOptionsResult.Fail(
                    $"Foundry model {model.Id} requires EnvironmentApiKey or AzureCli authentication.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
