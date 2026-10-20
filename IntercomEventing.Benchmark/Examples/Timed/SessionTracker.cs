namespace IntercomEventing.Benchmark.Examples.Timed;

/// <summary>
/// The payload delivered with every tick of a run: whose session it is.
/// </summary>
public sealed record SessionUser(string Name);

/// <summary>
/// Expires a session after a period without activity. Each activity restarts the countdown.
/// </summary>
public sealed class SessionTracker : IAsyncDisposable
{
    /// <summary>
    /// A one-shot timer (<c>autoReset: false</c>): it raises once per <c>Start</c>, and each <c>Start</c> replaces the
    /// countdown that is running.
    /// </summary>
    public TimedEvent<SessionUser> SessionExpired { get; }

    public SessionTracker(TimeSpan idleTimeout)
    {
        SessionExpired = new TimedEvent<SessionUser>(idleTimeout, autoReset: false);
    }

    public void RecordActivity(SessionUser user)
    {
        SessionExpired.Start(user, caller: this);
    }

    public ValueTask DisposeAsync()
    {
        return SessionExpired.DisposeAsync();
    }
}
