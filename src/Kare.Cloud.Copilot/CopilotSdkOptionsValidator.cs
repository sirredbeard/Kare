using Kare.Abstractions;
using Microsoft.Extensions.Options;

namespace Kare.Cloud.Copilot;

/// <summary>Validates conditional cloud configuration without reading secret values.</summary>
public sealed class CopilotSdkOptionsValidator : IValidateOptions<CopilotSdkOptions>
{
    private const decimal MaxAverageTokenPriceUsdPerMillion = 1_000_000m;

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
            if (model.AllowedModes.Count == 0 ||
                model.AllowedModes.Any(mode => mode is not (RouteMode.Personal or RouteMode.Work)))
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} must allow Personal or Work routing modes.");
            }

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

            if ((model.AverageInputCostUsdPerMillionTokens is { } inputPrice &&
                 (inputPrice < 0m || inputPrice > MaxAverageTokenPriceUsdPerMillion)) ||
                (model.AverageOutputCostUsdPerMillionTokens is { } outputPrice &&
                 (outputPrice < 0m || outputPrice > MaxAverageTokenPriceUsdPerMillion)))
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} average prices must be between zero and {MaxAverageTokenPriceUsdPerMillion} USD per million tokens.");
            }

            if (hasInputPrice &&
                (!Uri.TryCreate(model.PricingSource, UriKind.Absolute, out var pricingSource) ||
                 pricingSource.Scheme != Uri.UriSchemeHttps ||
                 pricingSource.UserInfo.Length > 0 ||
                 pricingSource.Query.Length > 0 ||
                 pricingSource.Fragment.Length > 0 ||
                 model.PricingAsOf is null))
            {
                return ValidateOptionsResult.Fail(
                    $"Cloud model {model.Id} requires a public HTTPS PricingSource without credentials, query, or fragment and a PricingAsOf date when prices are configured.");
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
