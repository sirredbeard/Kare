using Kare.Core;
using Kare.Core.Inference;
using Kare.Core.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kare.Tests;

public sealed class InferenceGateTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllowsOnlyTheConfiguredNumberOfConcurrentRequests()
    {
        using var gate = new InferenceGate(Options.Create(new InferenceLimits
        {
            MaxConcurrentInference = 2,
            MaxQueueDepth = 8,
        }));

        var first = await gate.AcquireAsync(TestToken);
        var second = await gate.AcquireAsync(TestToken);

        Assert.Equal(0, gate.AvailableSlots);

        first.Dispose();
        Assert.Equal(1, gate.AvailableSlots);

        second.Dispose();
        Assert.Equal(2, gate.AvailableSlots);
    }

    [Fact]
    public async Task ReleasingALeaseTwiceDoesNotCreateAnExtraSlot()
    {
        using var gate = new InferenceGate(Options.Create(new InferenceLimits
        {
            MaxConcurrentInference = 1,
        }));

        var lease = await gate.AcquireAsync(TestToken);
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, gate.AvailableSlots);
    }

    [Fact]
    public async Task RejectsOnceTheQueueIsFullInsteadOfWaitingWithoutBound()
    {
        using var gate = new InferenceGate(Options.Create(new InferenceLimits
        {
            MaxConcurrentInference = 1,
            MaxQueueDepth = 1,
            QueueTimeoutSeconds = 60,
        }));

        using var held = await gate.AcquireAsync(TestToken);

        // One waiter is allowed by the configured depth. It never completes because the
        // only slot stays held for the duration of the test.
        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var waiter = Task.Run(
            async () =>
            {
                using var lease = await gate.AcquireAsync(waiterCts.Token);
            },
            waiterCts.Token);

        await WaitForWaitersAsync(gate, 1);

        // The next caller is over the configured depth and must be rejected immediately.
        await Assert.ThrowsAsync<InferenceCapacityException>(async () =>
        {
            using var lease = await gate.AcquireAsync(TestToken);
        });

        await waiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public async Task RejectsWhenTheWaitExceedsTheQueueTimeout()
    {
        using var gate = new InferenceGate(Options.Create(new InferenceLimits
        {
            MaxConcurrentInference = 1,
            MaxQueueDepth = 4,
            QueueTimeoutSeconds = 1,
        }));

        using var held = await gate.AcquireAsync(TestToken);

        await Assert.ThrowsAsync<InferenceCapacityException>(async () =>
        {
            using var lease = await gate.AcquireAsync(TestToken);
        });
    }

    private static async Task WaitForWaitersAsync(InferenceGate gate, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (gate.Waiting < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestToken);
        }

        Assert.Equal(expected, gate.Waiting);
    }
}
