using Kare.Inference.GenieX;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class GenieXOptionsValidatorTests
{
    private readonly GenieXOptionsValidator _validator = new();

    [Fact]
    public void DisabledConfigurationDoesNotRequireAReachableServer()
    {
        var result = _validator.Validate(null, new GenieXOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledConfigurationRequiresLoopback()
    {
        var result = _validator.Validate(
            null,
            new GenieXOptions
            {
                Enabled = true,
                Endpoint = new Uri("http://192.0.2.1:18181/v1"),
            });

        Assert.True(result.Failed);
    }

    [Fact]
    public void PrecisionSuffixIsRejected()
    {
        var result = _validator.Validate(
            null,
            new GenieXOptions
            {
                Enabled = true,
                ModelId = "qualcomm/qwen3_1_7b:w4a16",
            });

        Assert.True(result.Failed);
    }

    [Fact]
    public void MeasuredBoardConfigurationIsAccepted()
    {
        var result = _validator.Validate(
            null,
            new GenieXOptions
            {
                Enabled = true,
                Endpoint = new Uri("http://127.0.0.1:18181/v1"),
                ModelId = "qualcomm/qwen3_1_7b",
                Priority = 100,
                ProbeTimeoutSeconds = 5,
            });

        Assert.True(result.Succeeded);
    }
}
