using Kare.Core.Options;
using Microsoft.Extensions.Options;

namespace Kare.Core.Inference;

/// <summary>
/// Bounds how many requests run inference at once and how many may wait.
/// The board cannot absorb an unbounded burst, so admission is explicit and a
/// rejected request fails fast instead of queueing into memory pressure.
/// </summary>
public sealed class InferenceGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly InferenceLimits _limits;
    private int _waiting;

    /// <summary>Creates the gate from configured limits.</summary>
    public InferenceGate(IOptions<InferenceLimits> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits.Value;
        _slots = new SemaphoreSlim(_limits.MaxConcurrentInference, _limits.MaxConcurrentInference);
    }

    /// <summary>Requests currently waiting for a slot.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Slots currently free.</summary>
    public int AvailableSlots => _slots.CurrentCount;

    /// <summary>
    /// Acquires an inference slot. Dispose the returned lease to release it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="InferenceCapacityException">
    /// The queue is already at <see cref="InferenceLimits.MaxQueueDepth"/>, or the wait
    /// exceeded <see cref="InferenceLimits.QueueTimeout"/>.
    /// </exception>
    public async ValueTask<Lease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A free slot means the caller never queues. Taking it here keeps holders out of
        // the queue depth accounting, so MaxQueueDepth means exactly what it says:
        // how many callers may be waiting, not how many may be in the gate.
        if (_slots.Wait(0, CancellationToken.None))
        {
            return new Lease(_slots);
        }

        // Reserve a queue position before waiting so depth is enforced, not just observed.
        var queued = Interlocked.Increment(ref _waiting);
        if (queued > _limits.MaxQueueDepth)
        {
            Interlocked.Decrement(ref _waiting);
            throw new InferenceCapacityException(_limits.MaxConcurrentInference, _limits.MaxQueueDepth);
        }

        try
        {
            if (!await _slots.WaitAsync(_limits.QueueTimeout, cancellationToken).ConfigureAwait(false))
            {
                throw new InferenceCapacityException(_limits.MaxConcurrentInference, _limits.MaxQueueDepth);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        return new Lease(_slots);
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// A held inference slot. Disposing releases it exactly once.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _slots;

        internal Lease(SemaphoreSlim slots) => _slots = slots;

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
