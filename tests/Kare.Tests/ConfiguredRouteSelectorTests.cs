using Kare.Abstractions;
using Kare.Core.Options;
using Kare.Core.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class ConfiguredRouteSelectorTests
{
    [Fact]
    public async Task ExplicitCloudModelSelectsBillableCloudRoute()
    {
        var selector = CreateSelector();

        var decision = await selector.SelectAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "kare-copilot" },
            TestContext.Current.CancellationToken);

        Assert.Equal(KareRoute.CopilotHeavy, decision.Route);
        Assert.True(decision.IsBillable);
        Assert.Equal(BackendKind.Remote, decision.Backend);
        Assert.Equal("auto", decision.ModelId);
    }

    [Fact]
    public async Task ExplicitLightModelSelectsFirstConfiguredCopilotModel()
    {
        var selector = CreateSelector();

        var decision = await selector.SelectAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "kare-fast" },
            TestContext.Current.CancellationToken);

        Assert.Equal(KareRoute.CopilotLight, decision.Route);
        Assert.Equal("mai-code-1.1-flash", decision.ModelId);
    }

    [Fact]
    public async Task AutomaticRouteStaysLocalBelowThreshold()
    {
        var selector = CreateSelector();

        var decision = await selector.SelectAsync(
            [new ChatMessage(ChatRole.User, "short")],
            new ChatOptions { ModelId = "kare-auto" },
            TestContext.Current.CancellationToken);

        Assert.Equal(KareRoute.LocalSlm, decision.Route);
        Assert.False(decision.IsBillable);
    }

    [Fact]
    public async Task AutomaticRouteEscalatesToLightTierAtConfiguredThreshold()
    {
        var selector = CreateSelector(moderateThreshold: 1_024);

        var decision = await selector.SelectAsync(
            [new ChatMessage(ChatRole.User, new string('x', 1_024))],
            new ChatOptions { ModelId = "kare-auto" },
            TestContext.Current.CancellationToken);

        Assert.Equal(KareRoute.CopilotLight, decision.Route);
    }

    [Fact]
    public async Task AutomaticRouteEscalatesToFoundryAtComplexThreshold()
    {
        var selector = CreateSelector(
            moderateThreshold: 1_024,
            complexThreshold: 2_048,
            includeComplex: true);

        var decision = await selector.SelectAsync(
            [new ChatMessage(ChatRole.User, new string('x', 2_048))],
            new ChatOptions { ModelId = "kare-auto" },
            TestContext.Current.CancellationToken);

        Assert.Equal(KareRoute.Foundry, decision.Route);
        Assert.Equal("foundry-complex", decision.ModelId);
    }

    private static ConfiguredRouteSelector CreateSelector(
        int moderateThreshold = 6_000,
        int complexThreshold = 24_000,
        bool includeComplex = false) =>
        new(
            Options.Create(new RoutePolicyOptions
            {
                EnableAutomaticCloudEscalation = true,
                ModeratePromptCharacterThreshold = moderateThreshold,
                ComplexPromptCharacterThreshold = complexThreshold,
            }),
            BackendKind.GenieXLlamaCpp,
            "qwen-local",
            new FakeCatalog(includeComplex));

    private sealed class FakeCatalog : ICloudModelCatalog
    {
        public FakeCatalog(bool includeComplex)
        {
            var models = new List<CloudModelDescriptor>
            {
                new("copilot-flash", "mai-code-1.1-flash", CloudModelProvider.GitHubCopilot, CloudModelTier.Fast, 0, true),
                new("copilot-terra", "mai-code-1.1-terra", CloudModelProvider.GitHubCopilot, CloudModelTier.Fast, 100, true),
                new("copilot-auto", "auto", CloudModelProvider.GitHubCopilot, CloudModelTier.Heavy, 0, true),
            };
            if (includeComplex)
            {
                models.Add(new(
                    "foundry-complex",
                    "foundry-complex",
                    CloudModelProvider.MicrosoftFoundry,
                    CloudModelTier.Complex,
                    0,
                    true));
            }

            Models = models;
        }

        public IReadOnlyList<CloudModelDescriptor> Models { get; }
    }
}
