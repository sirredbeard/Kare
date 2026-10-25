using Kare.CopilotLauncher;
using Xunit;

namespace Kare.Tests;

public sealed class LauncherVsCodeTests
{
    [Fact]
    public void VsCodeFlagIsALauncherOptionNotACopilotArgument()
    {
        var input = LauncherInputParser.Parse(["--vscode"]);

        Assert.True(input.VsCode);
        Assert.Empty(input.CopilotArguments);
        Assert.Null(input.Error);
    }

    [Fact]
    public void VsCodeFlagWorksWithAnExplicitDeviceHost()
    {
        var input = LauncherInputParser.Parse(["192.0.2.10", "--vscode"]);

        Assert.True(input.VsCode);
        Assert.Equal("192.0.2.10", input.DeviceHost);
        Assert.Empty(input.CopilotArguments);
    }

    [Fact]
    public void DefaultLaunchDoesNotEnterVsCodeMode()
    {
        var input = LauncherInputParser.Parse(["-i", "review this"]);

        Assert.False(input.VsCode);
        Assert.Equal(["-i", "review this"], input.CopilotArguments);
    }
}
