using HelpfulTypesAndExtensions;

namespace IntercomEventing.Benchmark.UserCreationExample;

public class UserCreation_EventSubscriber
{
    private Guid? UserCreatedEventFromUserCreationExample;
    
    public async Task HandleUserCreatedEventAsync(UserCreatedEventCall eventCall)
    {
        string callTime = eventCall.Metadata.LastEventTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        DebugHelp.DebugWriteLine($"Event call {eventCall.Metadata.EventCallId} for Event {eventCall.Metadata.EventId} called");
        if(eventCall.Metadata.EventCaller is UserCreation_EventProducer userCreation_EventProducer)
        {
            UserCreatedEventFromUserCreationExample = userCreation_EventProducer.UserCreatedEvent.Id;
        }
        else
        {
            UserCreatedEventFromUserCreationExample = null;
        }
        if (UserCreatedEventFromUserCreationExample.HasValue)
        {
            DebugHelp.DebugWriteLine($"Event call {eventCall.Metadata.EventCallId} was created by UserCreation_EventProducer");
        }
        //do something with the user
        await Task.Delay(100);
        Console.WriteLine($"Created {eventCall.User} at {callTime}");
    }
    
    public async Task HandleUserThresholdReachedEventAsync(UserThresholdReachedEventCall eventCall)
    {
        string callTime = eventCall.Metadata.LastEventTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        //do something with the user
        await Task.Delay(100);
        Console.WriteLine($"Login Threshold reached for {eventCall.User} at {callTime}");
    }
}