# IntercomEventing
The IntercomEventing library is a .NET library that provides a simple and efficient way to handle events in your application. It is designed to be easy to use and integrate into your existing codebase.

## Installation
- Install the IntercomEventing NuGet package in your project.
- Include the library with a using statement `using IntercomEventing;`
- Configure the eventing system with the `AddIntercomEventing` extension method.

## Features


- **Event Handling**: The library provides a simple and efficient way to handle events in your application.
- **Concurrency Control**: The library allows you to control the concurrency of event handlers, ensuring that they are executed in a controlled and predictable manner.
- **Handler Timeouts**: The library provides a way to set timeouts for event handlers, ensuring that after a preset time new event handlers can begin and previous event handlers can finish as a background task.
- **Event Metadata**: The library provides metadata for each event, including the event ID, event type, and event data. This metadata can be used to track and debug events in your application.
- **Event Types**: The library provides several types of events, including generic events, timed events, debounced events, and state change events. Each type of event is designed to handle specific use cases and provide the necessary functionality.
- **Event Handler Exceptions**: The library provides a way to handle exceptions that occur during event handling, ensuring that your application remains stable and responsive.
- **Independent Event Handling**: The library allows you to handle event calls independently of each other, ensuring that one event handler does not break another event handler.
- **Easily Disposable**: The library provides a way to dispose of events and their associated resources, ensuring that your application remains clean and efficient.
- **Event Subscription**: The library provides a way to subscribe to events and track their subscriptions, allowing you to define how the event should be handled and any necessary cleanup actions.


## Usage
To use the IntercomEventing library, you will need to install it via NuGet. Once installed, you can include the library with a using statement `using IntercomEventing;`

Every event is made up of an `event call` and an `event`.

An `event call` is a record that inherits from `EventCall<TEvent>` and contains the data that will be passed to the event handler. An event is a record that inherits from `GenericEvent<TEvent>` and contains the logic for raising the event.

An `event call` is different from the event itself because it contains a per invocation arguments / information while the event itself is a only created once and is raised multiple times.

### Basic Eventing Example 
An example event that is raised when a user is created
1. Define the event and event call

```csharp
public record UserCreatedEvent : GenericEvent<UserCreatedEvent>
{
   
    public void NotifyUserCreated(User user)
    {
        UserCreatedEventCall eventCall = CreateEventCall(user);
        RaiseEvent(eventCall);
    }
    
    /// <inheritdoc />
    override protected UserCreatedEventCall CreateEventCall(params object[]? args)
    {
        if (args?[0] is not User user)
        {
            throw new ArgumentException($"Args[0] is not type {typeof(User)}");
        }
        UserCreatedEventCall eventCall = new(user);
        return eventCall;
    }
}

public record UserCreatedEventCall(User User) : EventCall<UserCreatedEvent>;
```

1. (Version B) An alternative means of passing arguments to the event call is to use private fields inside the event class vs passing arguments into the array.
```csharp
public record UserCreatedEvent : GenericEvent<UserCreatedEvent>
{
    private User _user;
    public void NotifyUserCreated(User user)
    {
        _user = user;
        UserCreatedEventCall eventCall = CreateEventCall();
        RaiseEvent(eventCall);
    }
    
    /// <inheritdoc />
    override protected UserCreatedEventCall CreateEventCall(params object[]? args) => new(_user);
}

public record UserCreatedEventCall(User User) : EventCall<UserCreatedEvent>;
```


2. Define the event producer

```csharp
public class UserCreation_EventProducer
{
    public UserCreatedEvent UserCreatedEvent { get; init; } = new();

    public async Task CreateUserAsync(User user) => await UserCreatedEvent.RaiseEvent(new UserCreatedEventCall(user));
}
```

3. Define the event handler

**NOTE**: The event handler is defined as an async method that takes an `event call` as a parameter

```csharp
public class UserCreation_EventSubscriber
{
    public async Task HandleUserCreatedEventAsync(UserCreatedEventCall eventCall)
    {
        //do something with the user
        await Task.Delay(100);
        Console.WriteLine($"Created {eventCall.User}");
    }
}
```

4. Subscribe to the event

```csharp
UserCreation_EventSubscriber userCreationEventSubscriber = new();
await userCreationEventProducer.UserCreatedEvent.Subscribe<UserCreatedEventCall>(userCreationEventSubscriber.HandleUserCreatedEventAsync);
```

5. Raise the event

```csharp
User user1 = new("John Doe", "john.doe@example.com");
await userCreationEventProducer.CreateUserAsync(user1);
```

### Event Types
The library provides several types of events, including generic events, threshold events, timed events, debounced events, and state change events. 

Each type of event is designed to handle specific use cases and provide the necessary functionality.

#### Generic Event
A generic event is the base event type for all other events. It can be inherited from to create custom events.

**NOTE**: When inheriting from generic event, the derived class must call the RaiseEvent method for the event to be sent to the subscribers properly.

```csharp
public record UserCreatedEvent : GenericEvent<UserCreatedEvent>
{
    private User _user;
    
    public void NotifyUserCreated(User user)
    {
        _user = user;
        UserCreatedEventCall eventCall = CreateEventCall();
        RaiseEvent(eventCall);
    }
    
    /// <inheritdoc />
    override protected UserCreatedEventCall CreateEventCall() => new(_user);
}

public record UserCreatedEventCall(User User) : EventCall<UserCreatedEvent>;
```

#### Threshold Event
A threshold event is an event that tracks a value and raises an event when the value reaches or exceeds a threshold.

Threshold events automatically invoke the RaiseEvent call if the value reaches or exceeds the threshold, when the value is incremented.

```csharp
public record UserThresholdReachedEvent : ThresholdEvent<UserThresholdReachedEvent, int>(1)
{
    protected override ThresholdEventCall<UserThresholdReachedEvent, int> CreateEventCall() => new UserThresholdReachedEventCall();
}

public record UserThresholdReachedEventCall : ThresholdEventCall<UserThresholdReachedEvent,int>;
```

example incrementing the value of the event
```csharp
UserThresholdReachedEvent userThresholdReachedEvent = new();
await userThresholdReachedEvent.IncrementValue(1);
```

This would trigger the event handler since the value is 1 or greater.


#### Timed Event
A timed event is an event that is raised at a specified interval. The event will be optionally raised multiple times if auto reset is enabled.

```csharp
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
```

example starting the timer
```csharp
LogonSessionTimerEvent logonSessionTimerEvent = new();
logonSessionTimerEvent.Start(user);
```

#### Debounce Event
A debounce event is an event that debounces calls to the event handler. The event will only be raised after the debounce interval has passed without any new calls to the event handler.

```csharp
public record InputChangedEvent() : DebounceEvent<InputChangedEvent>(TimeSpan.FromMilliseconds(500))
{
    private UserInput? _lastInput;
    public async Task NotifyInputChanged(UserInput input)
    {
        _lastInput = input;
        await StartDebounce();
    }
    protected override DebounceEventCall<InputChangedEvent> CreateEventCall() => new InputChangedEventCall(_lastInput ?? throw new InvalidOperationException("No input available"));
}

public record InputChangedEventCall(UserInput Input) : DebounceEventCall<InputChangedEvent>;
```

example debouncing the event
```csharp
InputChangedEvent inputChangedEvent = new();
await inputChangedEvent.NotifyInputChanged(new UserInput("Hello World!"));
```


#### State Change Event
A state change event is an event that tracks state changes. The event will only be raised when the state changes.

```csharp
public record ServerPowerChangedEvent() : StateChangeEvent<ServerPowerChangedEvent, ServerPower>(ServerPower.Off)
{
    private Server _server;
    public async Task ChangeServerPowerState(Server server, ServerPower newState)
    {
        _server = server;
        CurrentState = newState;
    }
    protected override StateChangeEventCall<ServerPowerChangedEvent, ServerPower> CreateEventCall() => new ServerPowerChangedEventCall(_server);
}

public record ServerPowerChangedEventCall(Server Server) : StateChangeEventCall<ServerPowerChangedEvent, ServerPower>;
```

example changing the state
```csharp
ServerPowerChangedEvent serverPowerChangedEvent = new();
await serverPowerChangedEvent.ChangeServerPowerState(server, ServerPower.On);
```


### Eventing Options
The eventing system can be configured with the following options:
1. **SyncType**: Controls whether event handlers are executed sequentially or in parallel on a per event call basis. The default value is `EventingSyncType.Parallel`.
2. **MaxNumberOfConcurrentHandlers**: The maximum number of event handlers that can be executed at the same time on a per event call basis. The default values are 100 for parallel and 1 for sequential.
3. **StartNextEventHandlerAfter**: The time to wait before starting the next event handler. The default value is 1 second. The current event handler will continue to run within a Task in the background.
4. **AllowMultipleSubscribers**: If true, then more than one subscriber can subscribe to the same event, otherwise only one subscriber can subscribe to the event.


This can be configured via DI
```csharp
builder.Services.AddIntercomEventing(
    options =>
    {
        options.SyncType = EventingSyncType.Parallel;
        options.MaxNumberOfConcurrentHandlers = 100;
        options.StartNextEventHandlerAfter = TimeSpan.FromSeconds(1);
        options.DefaultExceptionHandler = ex => Console.WriteLine($"Default exception handler called with error {ex.Message}");
    });
```

### Handling Exceptions 
An event handler might throw an exception, typically with classic .NET events, these exceptions cannot be caught or handled outside the event handler.

With the IntercomEventing library, you can handle exceptions in 1 of 2 ways:
1. A global default exception handler can be set when configuring the eventing system.
```csharp
builder.Services.AddIntercomEventing(
    options =>
    {
        options.DefaultExceptionHandler = ex => Console.WriteLine($"Default exception handler called with error {ex.Message}");
    });
```
2. An exception handler can be provided when subscribing to an event. 
```csharp
await userCreationEventProducer.UserCreatedEvent.Subscribe<UserCreatedEventCall>(userCreationEventSubscriber.HandleUserCreatedEventAsync, exceptionHandler: ex => Console.WriteLine($"Event handler failed with error {ex.Message}"));
```


The exception handler will be called if an exception occurs during event handling. If one is provided when subscribing to an event, it will be used instead of the global default exception handler.
If no exception handler is provided when subscribing to an event, and no global default exception handler is set, exceptions will be suppressed and not propagated to the caller.









### Handler Timeout
By default, the system waits up to one second before starting the next round of event handler calls.
For example, if an event is raised twice with no delay in between the first call and the second call, the system will wait up to one second before starting the second round of event handler calls.

The timeout can be customized during DI with the `StartNextEventHandlerAfter` option.

Ex.
```csharp
builder.Services.AddIntercomEventing(
    options =>
    {
        options.StartNextEventHandlerAfter = TimeSpan.FromSeconds(1);
    });
```

`An event is invoked twice`
```csharp

await userCreationEventProducer.CreateUserAsync(user1);
await userCreationEventProducer.CreateUserAsync(user2);
```

`The event handler will be called twice with a 1 second delay in between each call, regardless of how long the event handler takes to complete`
```csharp
public async Task HandleUserCreatedEventAsync(UserCreatedEventCall eventCall)
{
    Console.WriteLine($"Event call {eventCall.Metadata.EventCallId} processing started at {DateTime.UtcNow}");
    await Task.Delay(5000);
    Console.WriteLine($"Event call {eventCall.Metadata.EventCallId} processing ended at {DateTime.UtcNow}");
}
```

The above code would print 
```csharp
Event call 1 processing started at  12:00:00
Event call 2 processing started at  12:00:01
Event call 1 processing ended at    12:00:05
Event call 2 processing ended at    12:00:06
```




## License
This library is licensed under the MIT License. See the LICENSE file for more information.