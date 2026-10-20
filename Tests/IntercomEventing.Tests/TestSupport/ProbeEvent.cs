namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// A custom event kind built the documented way (a class deriving from <see cref="IntercomEventBase{TCall}"/> that raises
/// through <c>DispatchAsync</c>). It exposes the protected members so tests can observe the base class contract: how often
/// the call factory runs, the disposal token, background failure routing and the event-kind shutdown hook.
/// </summary>
internal sealed class ProbeEvent : IntercomEventBase<EventCall<int>>
{
    private int _factoryInvocations;

    /// <summary>
    /// How many times a raise has called the call factory.
    /// </summary>
    public int FactoryInvocations => Volatile.Read(ref _factoryInvocations);

    /// <summary>
    /// Runs as the event kind's shutdown (<see cref="IntercomEventBase{TCall}.OnDisposingAsync"/>) when set.
    /// </summary>
    public Func<ProbeEvent, ValueTask>? OnDisposing { get; init; }

    /// <summary>
    /// The protected <see cref="IntercomEventBase{TCall}.DisposalToken"/>.
    /// </summary>
    public CancellationToken ExposedDisposalToken => DisposalToken;

    public ProbeEvent(EventingOptions? options)
        : base(options)
    {
    }

    /// <summary>
    /// Raises <paramref name="value"/> with a static factory that counts its invocations.
    /// </summary>
    public Task RaiseAsync(int value, object? caller = null, CancellationToken cancellationToken = default)
    {
        var raiseState = (Event: this, Value: value);
        return DispatchAsync(
            raiseState,
            static (state, metadata, callCancellationToken) =>
            {
                Interlocked.Increment(ref state.Event._factoryInvocations);
                return new EventCall<int>(state.Value, metadata, callCancellationToken);
            },
            caller,
            cancellationToken);
    }

    /// <summary>
    /// Raises with a factory of the caller's choosing, including <see langword="null"/> or one that throws.
    /// </summary>
    public Task RaiseWithFactoryAsync(EventCallFactory<int, EventCall<int>> createCall)
    {
        return DispatchAsync(1, createCall, caller: null, CancellationToken.None);
    }

    /// <summary>
    /// The protected <see cref="IntercomEventBase{TCall}.RouteBackgroundRaiseFailure"/>.
    /// </summary>
    public void ReportBackgroundFailure(Exception exception)
    {
        RouteBackgroundRaiseFailure(exception);
    }

    /// <summary>
    /// The protected <see cref="IntercomEventBase{TCall}.StartWithoutCallerContext"/>.
    /// </summary>
    public Task StartBackgroundWork(Func<Task> work)
    {
        return StartWithoutCallerContext(work);
    }

    protected override ValueTask OnDisposingAsync()
    {
        if (OnDisposing is null)
        {
            return ValueTask.CompletedTask;
        }
        return OnDisposing(this);
    }
}
