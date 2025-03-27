using IntercomEventing.Benchmark.UserCreationExample;
using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.TimedEventExample;

/// <summary>
/// Example of a timed event that fires when a user's logon session expires <br/>
/// It uses a 10 second timer and does not auto reset since it is a one-time event
/// </summary>
public record LogonSessionTimerEvent() : TimedEvent<LogonSessionTimerEvent>(TimeSpan.FromSeconds(10),false)
{
    private User _user;
    public void Start(User user)
    {
        _user = user;
        base.Start();
    }
    
    protected override TimedEventCall<LogonSessionTimerEvent> CreateEventCall() => new LogonSessionTimerEventCall(_user);
}

public record LogonSessionTimerEventCall(User User) : TimedEventCall<LogonSessionTimerEvent>;