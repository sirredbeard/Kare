namespace Kare.Core;

/// <summary>
/// Base type for errors Kare raises on purpose. These are control flow for the
/// gateway, not unexpected faults, and callers are expected to map them to a status code.
/// Kare does not catch broad exception types, so anything outside this hierarchy propagates.
/// </summary>
public abstract class KareException : Exception
{
    /// <summary>Creates the exception.</summary>
    protected KareException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner cause.</summary>
    protected KareException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// The request exceeded a configured input bound. The caller must shorten the request
/// or Kare must compact the context before retrying.
/// </summary>
public sealed class PromptTooLargeException : KareException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="actual">The measured size.</param>
    /// <param name="limit">The configured bound.</param>
    /// <param name="unit">The unit being bounded, such as characters or tokens.</param>
    public PromptTooLargeException(long actual, long limit, string unit)
        : base($"Prompt is {actual} {unit} and the configured limit is {limit} {unit}.")
    {
        Actual = actual;
        Limit = limit;
        Unit = unit;
    }

    /// <summary>The measured size.</summary>
    public long Actual { get; }

    /// <summary>The configured bound.</summary>
    public long Limit { get; }

    /// <summary>The unit being bounded.</summary>
    public string Unit { get; }
}

/// <summary>
/// Kare is already running its maximum concurrent inference and the wait queue is full.
/// Rejecting here is deliberate. An unbounded queue on this board turns into memory
/// pressure and thermal throttling instead of throughput.
/// </summary>
public sealed class InferenceCapacityException : KareException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="maxConcurrent">Configured concurrent inference limit.</param>
    /// <param name="maxQueueDepth">Configured queue depth.</param>
    public InferenceCapacityException(int maxConcurrent, int maxQueueDepth)
        : base($"All {maxConcurrent} inference slots are busy and the queue of {maxQueueDepth} is full.")
    {
        MaxConcurrent = maxConcurrent;
        MaxQueueDepth = maxQueueDepth;
    }

    /// <summary>Configured concurrent inference limit.</summary>
    public int MaxConcurrent { get; }

    /// <summary>Configured queue depth.</summary>
    public int MaxQueueDepth { get; }
}

/// <summary>
/// No local inference backend passed its availability probe. Kare reports this rather
/// than silently rerouting to a billable cloud provider.
/// </summary>
public sealed class NoBackendAvailableException : KareException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="detail">Per backend probe detail. Must not contain secrets.</param>
    public NoBackendAvailableException(string detail)
        : base($"No local inference backend is available. {detail}")
    {
    }
}
