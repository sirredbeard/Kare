using Kare.Abstractions;
using Kare.Cloud.Copilot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

/// <summary>
/// Regression tests for the routing-mode boundary. The configuration binder appends
/// to pre-filled collections, so a restricted AllowedModes list only survives
/// binding when the options default starts empty. With a pre-filled default the
/// bound result quietly allowed both modes and the Personal/Work filter never
/// restricted anything.
/// </summary>
public sealed class AllowedModesBindingTests
{
    [Fact]
    public void ConfiguredModeRestrictionSurvivesConfigurationBinding()
    {
        var backend = new CopilotSdkBackend(
            Options.Create(Bind()),
            NullLogger<CopilotSdkBackend>.Instance);

        var workOnly = backend.Models.Single(model => model.Id == "work-only");
        var unrestricted = backend.Models.Single(model => model.Id == "unrestricted");

        Assert.Equal(RouteMode.Work, workOnly.AllowedModes);
        Assert.Equal(RouteMode.Both, unrestricted.AllowedModes);
    }

    [Fact]
    public void ModePolicyHonorsTheBoundRestriction()
    {
        var backend = new CopilotSdkBackend(
            Options.Create(Bind()),
            NullLogger<CopilotSdkBackend>.Instance);
        var workOnly = backend.Models.Single(model => model.Id == "work-only");

        Assert.False(RouteModePolicy.Allows(workOnly, OptionsFor(RouteMode.Personal)));
        Assert.True(RouteModePolicy.Allows(workOnly, OptionsFor(RouteMode.Work)));
    }

    private static CopilotSdkOptions Bind()
    {
        var values = new Dictionary<string, string?>
        {
            [$"{CopilotSdkOptions.SectionName}:Enabled"] = "true",
            [$"{CopilotSdkOptions.SectionName}:Models:0:Id"] = "work-only",
            [$"{CopilotSdkOptions.SectionName}:Models:0:Provider"] = "Anthropic",
            [$"{CopilotSdkOptions.SectionName}:Models:0:ModelId"] = "claude-sonnet-5-5",
            [$"{CopilotSdkOptions.SectionName}:Models:0:AllowedModes:0"] = "Work",
            [$"{CopilotSdkOptions.SectionName}:Models:1:Id"] = "unrestricted",
            [$"{CopilotSdkOptions.SectionName}:Models:1:Provider"] = "GitHubCopilot",
            [$"{CopilotSdkOptions.SectionName}:Models:1:ModelId"] = "gpt-6-luna",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new CopilotSdkOptions();
        configuration.GetSection(CopilotSdkOptions.SectionName).Bind(options);
        return options;
    }

    private static Microsoft.Extensions.AI.ChatOptions OptionsFor(RouteMode mode) => new()
    {
        AdditionalProperties = new()
        {
            [RouteModePolicy.ModeOptionName] = mode.ToString(),
        },
    };
}
