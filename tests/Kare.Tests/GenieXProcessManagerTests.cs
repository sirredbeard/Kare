using System.Reflection;
using System.Runtime.InteropServices;
using Kare.Service.Inference;
using Xunit;

namespace Kare.Tests;

public sealed class GenieXProcessManagerTests
{
    [Fact]
    public void KillPInvokeTargetsTheLowercaseLibcEntryPoint()
    {
        // The libc export is 'kill'. The managed method name is 'Kill', so the
        // entry point has to be named in the attribute. Without it the stop
        // path threw EntryPointNotFoundException during service shutdown.
        var method = typeof(GenieXProcessManager).GetMethod(
            "Kill",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var attribute = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("libc", attribute.Value);
        Assert.Equal("kill", attribute.EntryPoint);
    }
}
