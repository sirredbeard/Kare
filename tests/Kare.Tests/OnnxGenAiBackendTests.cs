using Kare.Core;
using Kare.Inference.OnnxGenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class OnnxGenAiBackendTests
{
    [Fact]
    public async Task ToolRequestFailsBeforeModelLoading()
    {
        using var backend = new OnnxGenAiBackend(
            Options.Create(new OnnxGenAiOptions()),
            NullLogger<OnnxGenAiBackend>.Instance);
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "result", "test_tool")],
        };

        var exception = await Assert.ThrowsAsync<UnsupportedBackendCapabilityException>(
            () => backend.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Use the tool.")],
                options,
                TestContext.Current.CancellationToken));

        Assert.Equal("tool calling", exception.Capability);
    }

    [Fact]
    public void EmptyModelPathIsAValidDisabledFallbackConfiguration()
    {
        var validator = new Kare.Service.Options.OnnxGenAiOptionsValidator();

        var result = validator.Validate(null, new OnnxGenAiOptions());

        Assert.True(result.Succeeded);
    }
}
