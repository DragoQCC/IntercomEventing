namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Collects the failures an event reports to <see cref="EventingOptions.DefaultExceptionHandler"/>, from any thread.
/// </summary>
/// <example>
/// <code>
/// var failures = new FailureRecorder();
/// var evt = new IntercomEvent&lt;int&gt;(new EventingOptions { DefaultExceptionHandler = failures.Record });
/// </code>
/// </example>
internal sealed class FailureRecorder
{
    private readonly Lock _failuresLock = new();
    private readonly List<EventHandlerFailure> _failures = [];

    /// <summary>
    /// Number of failures recorded so far.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_failuresLock)
            {
                return _failures.Count;
            }
        }
    }

    /// <summary>
    /// A copy of the failures recorded so far, in the order they arrived.
    /// </summary>
    public IReadOnlyList<EventHandlerFailure> Failures
    {
        get
        {
            lock (_failuresLock)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <summary>
    /// Pass this method as <see cref="EventingOptions.DefaultExceptionHandler"/>.
    /// </summary>
    public void Record(EventHandlerFailure failure)
    {
        lock (_failuresLock)
        {
            _failures.Add(failure);
        }
    }

    /// <summary>
    /// The failures recorded so far for one stage.
    /// </summary>
    public IReadOnlyList<EventHandlerFailure> ForStage(EventFailureStage stage)
    {
        return Failures.Where(failure => failure.Stage == stage).ToArray();
    }
}
