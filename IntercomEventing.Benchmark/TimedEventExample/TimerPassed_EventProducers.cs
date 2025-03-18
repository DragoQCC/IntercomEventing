using IntercomEventing.Benchmark.UserCreationExample;

namespace IntercomEventing.Benchmark.TimedEventExample;

public class TimerPassed_EventProducer
{
    public LogonSessionTimerEvent LogonSessionTimerEvent { get; init; } = new();
    
    public async Task TrackUserLogonSession(User user) => LogonSessionTimerEvent.Start(user);
}