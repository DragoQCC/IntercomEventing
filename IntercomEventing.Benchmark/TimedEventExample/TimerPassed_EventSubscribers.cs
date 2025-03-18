namespace IntercomEventing.Benchmark.TimedEventExample;

public class TimerPassed_EventSubscribers
{
    public async Task HandleLogonSessionExpiredAsync(LogonSessionTimerEventCall eventCall)
    {
        string callTime = eventCall.Metadata.LastEventTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Console.WriteLine($"User {eventCall.User.Name} logon session timed out after {eventCall.Interval.Seconds} seconds at {callTime}");
        //do something about the user session ending like cause a page refresh
        await Task.Delay(100);
    }
}
