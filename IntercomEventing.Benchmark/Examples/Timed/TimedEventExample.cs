namespace IntercomEventing.Benchmark.Examples.Timed;

/// <summary>
/// <c>TimedEvent</c>: a one-shot idle timeout that each activity restarts, and a repeating heartbeat that a handler stops.
/// </summary>
internal sealed class TimedEventExample : IExample
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(50);

    //generous upper bound for a tick to arrive on a busy machine
    private static readonly TimeSpan TickWaitLimit = TimeSpan.FromSeconds(5);

    //long enough for a further tick of either timer, had one been scheduled
    private static readonly TimeSpan QuietPeriodCheck = TimeSpan.FromMilliseconds(400);

    public string Name => "Timed";

    public string Summary => "TimedEvent: one-shot timeouts and repeating ticks";

    public async Task RunAsync()
    {
        await RunSessionTimeoutAsync();
        await RunHeartbeatAsync();
    }

    private static async Task RunSessionTimeoutAsync()
    {
        await using var sessions = new SessionTracker(IdleTimeout);
        var expiredSessions = new List<TimedEventCall<SessionUser>>();
        var firstExpiry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExpiry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessions.SessionExpired.Subscribe(call =>
        {
            int expiredCount;
            lock (expiredSessions)
            {
                expiredSessions.Add(call);
                expiredCount = expiredSessions.Count;
            }
            Console.WriteLine($"Session of {call.Payload.Name} expired after {call.Interval.TotalMilliseconds} ms without activity (tick {call.TickNumber})");
            if (expiredCount == 1)
            {
                firstExpiry.TrySetResult();
            }
            else
            {
                secondExpiry.TrySetResult();
            }
        });

        ExampleCheck.Step("Two activities in a row: the second restarts the countdown");
        sessions.RecordActivity(new SessionUser("Ada"));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        sessions.RecordActivity(new SessionUser("Ada"));
        await firstExpiry.Task.WaitAsync(TickWaitLimit);
        await Task.Delay(QuietPeriodCheck);

        ExampleCheck.That(CountExpiredSessions(expiredSessions) == 1, "the restarted one-shot timer raised exactly once");
        ExampleCheck.That(!sessions.SessionExpired.IsRunning, "a one-shot timer stops after its tick");

        ExampleCheck.Step("Activity after the session expired starts a new countdown");
        sessions.RecordActivity(new SessionUser("Grace"));
        await secondExpiry.Task.WaitAsync(TickWaitLimit);

        TimedEventCall<SessionUser>[] recordedExpiries;
        lock (expiredSessions)
        {
            recordedExpiries = [.. expiredSessions];
        }
        ExampleCheck.That(recordedExpiries[1].Payload.Name == "Grace", "the new countdown delivered its own payload");
        //the first countdown did tick, so a TickNumber that kept counting across Starts would show 2 here
        var everyRunStartedAtTickOne = recordedExpiries.All(expiry => expiry.TickNumber == 1);
        ExampleCheck.That(everyRunStartedAtTickOne, "TickNumber counts from 1 for each Start, also after an earlier Start ticked");
    }

    private static int CountExpiredSessions(List<TimedEventCall<SessionUser>> expiredSessions)
    {
        lock (expiredSessions)
        {
            return expiredSessions.Count;
        }
    }

    private static async Task RunHeartbeatAsync()
    {
        //payload-less and repeating (autoReset defaults to true)
        await using var heartbeat = new TimedEvent(HeartbeatInterval);
        var tickNumbers = new List<long>();
        var stoppedAtThirdTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        heartbeat.Subscribe(call =>
        {
            lock (tickNumbers)
            {
                tickNumbers.Add(call.TickNumber);
            }
            Console.WriteLine($"Heartbeat tick {call.TickNumber}");
            if (call.TickNumber == 3)
            {
                //Stop never waits, so it is safe inside a handler; this tick still reaches every handler
                heartbeat.Stop();
                stoppedAtThirdTick.TrySetResult();
            }
        });

        ExampleCheck.Step($"A heartbeat every {HeartbeatInterval.TotalMilliseconds} ms, stopped by its own handler at tick 3");
        heartbeat.Start();
        await stoppedAtThirdTick.Task.WaitAsync(TickWaitLimit);
        await Task.Delay(QuietPeriodCheck);

        long[] recordedTicks;
        lock (tickNumbers)
        {
            recordedTicks = [.. tickNumbers];
        }
        ExampleCheck.That(recordedTicks.SequenceEqual([1L, 2L, 3L]), "ticks 1, 2 and 3 arrived and none after Stop()");
        ExampleCheck.That(!heartbeat.IsRunning, "IsRunning is false after Stop()");
    }
}
