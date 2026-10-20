using System.Collections.Concurrent;

namespace IntercomEventing.Blazor.Tests.TestSupport;

/// <summary>
/// Replaces <see cref="EventingOptions.Default"/> with one whose exception handler records the failures of one event, and
/// restores the previous default when disposed.
/// </summary>
/// <remarks>
/// <para>The default is process-wide, so tests that use this scope are marked <c>[NotInParallel(DefaultExceptionHandlerScope.NotInParallelKey)]</c>,
/// and the handler records only the failures of the event named here, so other tests running meanwhile do not interfere.</para>
/// <para>Only events created without options of their own report to it; use <see cref="EventFailureRecorder"/> for an event
/// with its own options, or this scope to check that such an event's failures stay away from the default.</para>
/// </remarks>
public sealed class DefaultExceptionHandlerScope : IDisposable
{
    public const string NotInParallelKey = "EventingOptions.Default";

    private readonly EventingOptions _previousDefault;
    private readonly string _eventName;
    private readonly ConcurrentQueue<EventHandlerFailure> _failures = new();
    private readonly TaskCompletionSource<EventHandlerFailure> _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<EventHandlerFailure> Failures => _failures;

    /// <param name="eventName">Only failures of the event with this name are recorded.</param>
    public DefaultExceptionHandlerScope(string eventName)
    {
        _eventName = eventName;
        _previousDefault = EventingOptions.Default;
        EventingOptions.Default = _previousDefault with
        {
            DefaultExceptionHandler = RecordFailure,
        };
    }

    public Task<EventHandlerFailure> WaitForFirstFailureAsync()
    {
        return _firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        EventingOptions.Default = _previousDefault;
    }

    private void RecordFailure(EventHandlerFailure failure)
    {
        if (failure.EventName != _eventName)
        {
            return;
        }
        _failures.Enqueue(failure);
        _firstFailure.TrySetResult(failure);
    }
}
