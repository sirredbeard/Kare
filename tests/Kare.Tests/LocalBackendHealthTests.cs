using System.Runtime.CompilerServices;
using Kare.Abstractions;
using Kare.Core.Inference;
using Kare.Service;
using Kare.Service.Inference;
using Kare.Service.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class LocalBackendHealthTests
{
    [Fact]
    public void DisabledOwnedProcessDoesNotRequireDevicePaths()
    {
        var result = new GenieXProcessOptionsValidator()
            .Validate(null, new GenieXProcessOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OwnedProcessRequiresValidatedFixedPathsAndCompute()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "kare-geniex-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "geniex");
        File.WriteAllText(executable, string.Empty);

        try
        {
            var validator = new GenieXProcessOptionsValidator();
            var options = new GenieXProcessOptions
            {
                Enabled = true,
                ExecutablePath = executable,
                WorkingDirectory = directory,
                DataDirectory = directory,
                Compute = "npu",
            };

            Assert.True(validator.Validate(null, options).Succeeded);

            options.Compute = "npu; reboot";
            Assert.False(validator.Validate(null, options).Succeeded);
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task FailedNpuRequestRetriesCpuAndWarnsOnce()
    {
        var primary = new FakeLocalBackend(
            BackendKind.GenieXQairt,
            100,
            BackendProbeResult.Available("ready"),
            _ => throw Failure("NPU failed."));
        var fallback = new FakeLocalBackend(
            BackendKind.OnnxGenAiCpu,
            0,
            BackendProbeResult.Available("ready"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "cpu answer")));
        var selected = CreateSelected(primary, fallback);
        using var monitor = CreateMonitor(selected);
        using var dispatch = new SelectedBackendChatClient(
            selected,
            monitor,
            NullLogger<SelectedBackendChatClient>.Instance);
        using var client = new LocalBackendWarningChatClient(dispatch, selected);

        var first = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "status")],
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "status")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("! Kare's local NPU is unavailable.", first.Text, StringComparison.Ordinal);
        Assert.EndsWith("cpu answer", first.Text, StringComparison.Ordinal);
        Assert.Equal("cpu answer", second.Text);
        Assert.True(selected.IsCpuFallback);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(2, fallback.CallCount);
    }

    [Fact]
    public async Task StreamingFallbackWarningPrecedesCpuOutput()
    {
        var primary = new FakeLocalBackend(
            BackendKind.GenieXQairt,
            100,
            BackendProbeResult.Available("ready"),
            _ => throw Failure("NPU failed."));
        var fallback = new FakeLocalBackend(
            BackendKind.OnnxGenAiCpu,
            0,
            BackendProbeResult.Available("ready"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "cpu answer")));
        var selected = CreateSelected(primary, fallback);
        using var monitor = CreateMonitor(selected);
        using var dispatch = new SelectedBackendChatClient(
            selected,
            monitor,
            NullLogger<SelectedBackendChatClient>.Instance);
        using var client = new LocalBackendWarningChatClient(dispatch, selected);

        var updates = await client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "status")],
                cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith(
            "! Kare's local NPU is unavailable.",
            updates[0].Text,
            StringComparison.Ordinal);
        Assert.Equal("cpu answer", updates[1].Text);
    }

    [Fact]
    public async Task ConsecutiveHealthyProbesReturnToNpu()
    {
        var probes = new Queue<BackendProbeResult>(
        [
            BackendProbeResult.Unavailable("FastRPC mapping failed."),
            BackendProbeResult.Available("ready"),
            BackendProbeResult.Available("ready"),
        ]);
        var primary = new FakeLocalBackend(
            BackendKind.GenieXQairt,
            100,
            () => probes.Dequeue(),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "npu")));
        var fallback = new FakeLocalBackend(
            BackendKind.OnnxGenAiCpu,
            0,
            BackendProbeResult.Available("ready"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "cpu")));
        var selected = CreateSelected(primary, fallback);
        using var monitor = CreateMonitor(selected, consecutiveSuccesses: 2);

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(selected.IsCpuFallback);

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(selected.IsCpuFallback);

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.False(selected.IsDegraded);
        Assert.Equal(BackendKind.GenieXQairt, selected.Kind);
    }

    [Fact]
    public async Task CancellationMovesLocalWorkOffNpu()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var primary = new FakeLocalBackend(
            BackendKind.GenieXQairt,
            100,
            BackendProbeResult.Available("ready"),
            _ => throw new OperationCanceledException(source.Token));
        var fallback = new FakeLocalBackend(
            BackendKind.OnnxGenAiCpu,
            0,
            BackendProbeResult.Available("ready"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "cpu")));
        var selected = CreateSelected(primary, fallback);
        using var monitor = CreateMonitor(selected);
        using var client = new SelectedBackendChatClient(
            selected,
            monitor,
            NullLogger<SelectedBackendChatClient>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "status")],
                cancellationToken: source.Token));

        Assert.True(selected.IsCpuFallback);
        Assert.Equal(BackendKind.OnnxGenAiCpu, selected.Kind);
    }

    [Fact]
    public async Task SuccessfulNpuRequestClearsDegradedStateWithoutCpuFallback()
    {
        var primary = new FakeLocalBackend(
            BackendKind.GenieXQairt,
            100,
            BackendProbeResult.Available("ready"),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "npu")));
        var selected = new SelectedBackend();
        selected.Set(
            new LocalBackendSelection(
                primary,
                [
                    new BackendProbe(
                        primary.Kind,
                        primary.Priority,
                        BackendProbeResult.Available("ready")),
                ]),
            [primary]);
        Assert.False(selected.MarkDegraded("FastRPC probe failed."));
        using var monitor = CreateMonitor(selected);
        using var client = new SelectedBackendChatClient(
            selected,
            monitor,
            NullLogger<SelectedBackendChatClient>.Instance);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "status")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("npu", response.Text);
        Assert.False(selected.IsDegraded);
    }

    private static SelectedBackend CreateSelected(
        ILocalInferenceBackend primary,
        ILocalInferenceBackend fallback)
    {
        var selected = new SelectedBackend();
        selected.Set(
            new LocalBackendSelection(
                primary,
                [
                    new BackendProbe(
                        primary.Kind,
                        primary.Priority,
                        BackendProbeResult.Available("ready")),
                    new BackendProbe(
                        fallback.Kind,
                        fallback.Priority,
                        BackendProbeResult.Available("ready")),
                ]),
            [primary, fallback]);
        return selected;
    }

    private static LocalBackendHealthMonitor CreateMonitor(
        SelectedBackend selected,
        int consecutiveSuccesses = 2) =>
        new(
            selected,
            new FakeRecovery(),
            Options.Create(new LocalBackendHealthOptions
            {
                ConsecutiveRecoverySuccesses = consecutiveSuccesses,
            }),
            NullLogger<LocalBackendHealthMonitor>.Instance);

    private static LocalInferenceException Failure(string message) =>
        new(message, new InvalidOperationException(message));

    private sealed class FakeRecovery : ILocalBackendRecovery
    {
        public bool Supports(BackendKind backend) => false;

        public Task<bool> TryRecoverAsync(
            BackendKind backend,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeLocalBackend : ILocalInferenceBackend
    {
        private readonly Func<BackendProbeResult> _probe;
        private readonly Func<ChatOptions?, ChatResponse> _response;

        public FakeLocalBackend(
            BackendKind kind,
            int priority,
            BackendProbeResult probe,
            Func<ChatOptions?, ChatResponse> response)
            : this(kind, priority, () => probe, response)
        {
        }

        public FakeLocalBackend(
            BackendKind kind,
            int priority,
            Func<BackendProbeResult> probe,
            Func<ChatOptions?, ChatResponse> response)
        {
            Kind = kind;
            Priority = priority;
            _probe = probe;
            _response = response;
        }

        public BackendKind Kind { get; }

        public int Priority { get; }

        public int CallCount { get; private set; }

        public ValueTask<BackendProbeResult> ProbeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_probe());
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_response(options));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var response = _response(options);
            await Task.Yield();
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Text);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
