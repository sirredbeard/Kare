using Kare.CopilotLauncher;
using Xunit;

namespace Kare.Tests;

public sealed class CopilotLauncherTests
{
    [Fact]
    public void ParseTreatsCopilotOptionAsCopilotArgument()
    {
        var result = LauncherInputParser.Parse(["-i", "Review this repository"]);

        Assert.Null(result.DeviceHost);
        Assert.Equal(["-i", "Review this repository"], result.CopilotArguments);
        Assert.False(result.ShowHelp);
        Assert.False(result.Verbose);
        Assert.False(result.MinimalContext);
        Assert.False(result.WorkMode);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseSeparatesExplicitDeviceHostFromCopilotArguments()
    {
        var result = LauncherInputParser.Parse(["device.example", "-i", "Review this repository"]);

        Assert.Equal("device.example", result.DeviceHost);
        Assert.Equal(["-i", "Review this repository"], result.CopilotArguments);
        Assert.False(result.ShowHelp);
    }

    [Fact]
    public void ParseSeparatesLauncherDiagnosticsFromCopilotArguments()
    {
        var result = LauncherInputParser.Parse(
            ["--kare-verbose", "--kare-log-dir", "/tmp/kare-logs", "--kare-minimal-context", "-p", "test"]);

        Assert.True(result.Verbose);
        Assert.Equal("/tmp/kare-logs", result.LogDirectory);
        Assert.True(result.MinimalContext);
        Assert.False(result.WorkMode);
        Assert.Equal(["-p", "test"], result.CopilotArguments);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseRejectsMissingLauncherLogDirectory()
    {
        var result = LauncherInputParser.Parse(["--kare-log-dir"]);

        Assert.Equal("--kare-log-dir requires a path.", result.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--kare-verbose")]
    public void ParsePreservesCopilotOptionValues(string value)
    {
        var result = LauncherInputParser.Parse(["-p", value]);

        Assert.False(result.ShowHelp);
        Assert.False(result.Verbose);
        Assert.Equal(["-p", value], result.CopilotArguments);
    }

    [Fact]
    public void ParseHonorsPassThroughBoundary()
    {
        var result = LauncherInputParser.Parse(["--kare-verbose", "--", "--help"]);

        Assert.True(result.Verbose);
        Assert.False(result.ShowHelp);
        Assert.Equal(["--help"], result.CopilotArguments);
    }

    [Fact]
    public void ParseSupportsExplicitWorkMode()
    {
        var result = LauncherInputParser.Parse(["--kare-work", "-i", "Review this repository"]);

        Assert.True(result.WorkMode);
        Assert.Equal(["-i", "Review this repository"], result.CopilotArguments);
        Assert.Null(result.Error);
    }

    [Fact]
    public void WorkModeMatchesProtectedRepositoryAndOrganizationMappings()
    {
        var config = new Dictionary<string, string>
        {
            ["KARE_WORK_REPOSITORIES"] = "sirredbeard/Kare",
            ["KARE_WORK_ORGANIZATIONS"] = "example-work",
        };

        var repository = RoutingModeResolver.Resolve(
            explicitWork: false,
            config,
            [new GitRemote("sirredbeard", "Kare", false)]);
        var organization = RoutingModeResolver.Resolve(
            explicitWork: false,
            config,
            [new GitRemote("example-work", "project", false)]);

        Assert.Equal(Kare.Abstractions.RouteMode.Work, repository.Mode);
        Assert.Equal("protected_mapping", repository.Reason);
        Assert.Equal(Kare.Abstractions.RouteMode.Work, organization.Mode);
        Assert.Equal("org_match", organization.Reason);
    }

    [Fact]
    public void WorkModeUsesUpstreamMatchForAWorkOwnedUpstream()
    {
        var result = RoutingModeResolver.Resolve(
            explicitWork: false,
            new Dictionary<string, string>(),
            [new GitRemote("dotnet", "runtime", true)]);

        Assert.Equal(Kare.Abstractions.RouteMode.Work, result.Mode);
        Assert.Equal("upstream_match", result.Reason);
    }

    [Fact]
    public void SshTunnelWaitsForAuthenticationBeforeBackgrounding()
    {
        var arguments = CopilotKareApp.BuildSshArguments(
            "arduino",
            "device.example",
            "5285",
            "5285",
            "/tmp/kare-control.sock");

        Assert.Contains("-M", arguments);
        Assert.Contains("-f", arguments);
        Assert.Contains("-S", arguments);
        Assert.Contains("/tmp/kare-control.sock", arguments);
        Assert.Contains("-o", arguments);
        Assert.Contains("ExitOnForwardFailure=yes", arguments);
    }

    [Fact]
    public void ResolvePrefersExplicitDeviceHostOverEnvironmentAndConfig()
    {
        const string environmentVariable = "KARE_DEVICE_HOST";
        var originalValue = Environment.GetEnvironmentVariable(environmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(environmentVariable, "environment.example");
            var config = new Dictionary<string, string>
            {
                ["KARE_DEVICE_HOST"] = "config.example",
            };

            var result = DeviceHostResolver.Resolve(
                "explicit.example",
                config,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

            Assert.Equal("explicit.example", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, originalValue);
        }
    }

    [Fact]
    public void ResolveUsesLastDeviceHostWhenNoOtherValueExists()
    {
        const string hostEnvironmentVariable = "KARE_DEVICE_HOST";
        const string ipEnvironmentVariable = "KARE_DEVICE_IP";
        var originalHost = Environment.GetEnvironmentVariable(hostEnvironmentVariable);
        var originalIp = Environment.GetEnvironmentVariable(ipEnvironmentVariable);
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var lastDeviceHostFile = Path.Combine(tempDirectory, "last-device-host");

        try
        {
            Environment.SetEnvironmentVariable(hostEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(ipEnvironmentVariable, null);
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(lastDeviceHostFile, "remembered.example");

            var result = DeviceHostResolver.Resolve(
                null,
                new Dictionary<string, string>(),
                lastDeviceHostFile);

            Assert.Equal("remembered.example", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostEnvironmentVariable, originalHost);
            Environment.SetEnvironmentVariable(ipEnvironmentVariable, originalIp);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ConfigFileRejectsMalformedEntries()
    {
        var configFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(configFile, "KARE_API_KEY");

            var exception = Assert.Throws<InvalidDataException>(() => ConfigFile.Load(configFile));

            Assert.Contains(":1", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configFile);
        }
    }
}
