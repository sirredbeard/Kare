using Kare.Cloud.Copilot;
using Xunit;

namespace Kare.Tests;

public sealed class CopilotSdkOptionsValidatorTests
{
    private readonly CopilotSdkOptionsValidator _validator = new();

    [Fact]
    public void DisabledConfigurationNeedsNoCredentialsOrEndpoint()
    {
        var result = _validator.Validate(null, new CopilotSdkOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FoundryRequiresHttpsEndpoint()
    {
        var result = _validator.Validate(
            null,
            new CopilotSdkOptions
            {
                Enabled = true,
                Provider = CopilotCloudProvider.MicrosoftFoundry,
                FoundryBaseUrl = "http://foundry.example/v1",
            });

        Assert.True(result.Failed);
    }

    [Fact]
    public void AzureCliFoundryRouteAcceptsProtectedBearerConfiguration()
    {
        var options = new CopilotSdkOptions { Enabled = true };
        options.Models.Add(new CloudModelRouteOptions
        {
            Id = "foundry-complex",
            Provider = Kare.Abstractions.CloudModelProvider.MicrosoftFoundry,
            ModelId = "claude-opus-5",
            WireModel = "deployment-one",
            BaseUrl = "https://foundry.example",
            Authentication = CloudAuthentication.AzureCli,
            WireApi = "anthropic",
        });

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FoundryRouteRequiresSeparateWireModel()
    {
        var options = new CopilotSdkOptions { Enabled = true };
        options.Models.Add(new CloudModelRouteOptions
        {
            Id = "foundry-complex",
            Provider = Kare.Abstractions.CloudModelProvider.MicrosoftFoundry,
            ModelId = "claude-opus-5",
            BaseUrl = "https://foundry.example",
            Authentication = CloudAuthentication.AzureCli,
            WireApi = "anthropic",
        });

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void CloudModelsRequireUniqueRouteId()
    {
        var options = new CopilotSdkOptions { Enabled = true };
        options.Models.Add(new CloudModelRouteOptions
        {
            Id = "complex",
            Provider = Kare.Abstractions.CloudModelProvider.MicrosoftFoundry,
            ModelId = "deployment-one",
            BaseUrl = "https://foundry.example/v1",
            ApiKeyEnvironmentVariable = "TEST_FOUNDRY_KEY",
        });
        options.Models.Add(new CloudModelRouteOptions
        {
            Id = "complex",
            Provider = Kare.Abstractions.CloudModelProvider.MicrosoftFoundry,
            ModelId = "deployment-two",
            BaseUrl = "https://foundry.example/v1",
            ApiKeyEnvironmentVariable = "TEST_FOUNDRY_KEY",
        });

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}
