using System.Collections.Concurrent;

namespace IntercomEventing.Blazor.Tests.TestSupport;

/// <summary>
/// Records the failures one event reports to the <see cref="EventingOptions.DefaultExceptionHandler"/> of its own options.
/// Unlike <see cref="DefaultExceptionHandlerScope"/> it changes no process-wide state, so tests that use it can run in parallel.
/// </summary>
public sealed class EventFailureRecorder
{
    private readonly bool _throwAfterRecording;
    private readonly ConcurrentQueue<EventHandlerFailure> _failures = new();
    private readonly TaskCompletionSource<EventHandlerFailure> _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<EventHandlerFailure> Failures => _failures;

    /// <summary>
    /// <see cref="TestEventOptions.WaitForEveryHandler"/> with this recorder as the event's own exception handler.
    /// </summary>
    public EventingOptions EventOptions { get; }

    /// <param name="throwAfterRecording"><see langword="true"/> makes the handler throw after recording each failure, like a
    /// faulty logging callback.</param>
    public EventFailureRecorder(bool throwAfterRecording = false)
    {
        _throwAfterRecording = throwAfterRecording;
        EventOptions = TestEventOptions.WaitForEveryHandler with
        {
            DefaultExceptionHandler = RecordFailure,
        };
    }

    public Task<EventHandlerFailure> WaitForFirstFailureAsync()
    {
        return _firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void RecordFailure(EventHandlerFailure failure)
    {
        _failures.Enqueue(failure);
        _firstFailure.TrySetResult(failure);
        if (_throwAfterRecording)
        {
            throw new InvalidOperationException("the event's exception handler failed");
        }
    }
}
