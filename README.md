# IntercomEventing
The IntercomEventing library is a .NET library that provides a simple and efficient way to handle events in your application. It replaces the classic `event Func<T, Task>` with typed, awaitable events, and it is designed to be easy to use and integrate into your existing codebase.

## Installation
- Install the IntercomEventing NuGet package in your project: `dotnet add package IntercomEventing --prerelease`
- For Blazor apps, install the IntercomEventing.Blazor package instead, it includes IntercomEventing: `dotnet add package IntercomEventing.Blazor --prerelease`
- Include the library with a using statement `using IntercomEventing;` (the Blazor types are in `IntercomEventing.Blazor`)
- Optionally configure the default eventing options with the `AddIntercomEventing` extension method.

**NOTE**: The library targets `net11.0`. The 2.0 previews are built with the .NET 11 release candidate SDK, so the packages stay prereleases until .NET 11 is released. IntercomEventing has no package dependencies, and its source generator is included in the package, so there is nothing extra to install.

## Features
- **Typed Event Calls**: Every time an event is raised, the library creates one event call that carries your data and the metadata for that raise. Handlers receive the call type of their event, so a handler for the wrong event does not compile.
- **Awaitable Events**: Raising an event returns a task that completes when every handler has finished, or has been moved to the background after the handler timeout.
- **Concurrency Control**: The library allows you to control the concurrency of event handlers, running them in parallel or one after another, with a limit on how many run at the same time.
- **Handler Timeouts**: The library provides a way to set timeouts for event handlers, ensuring that after a preset time new event handlers can begin and previous event handlers can finish as a background task.
- **Event Metadata**: The library provides metadata for each event call, including the event ID, event name, call ID, the time it was raised and who raised it. This metadata can be used to track and debug events in your application.
- **Event Types**: The library provides several types of events, including generic events, threshold events, state change events, timed events and debounced events. You can also build your own event types.
- **Event Handler Exceptions**: The library provides a way to handle exceptions that occur during event handling, and an exception thrown by a handler never reaches the code that raised the event.
- **Independent Event Handling**: The library allows you to handle event calls independently of each other, ensuring that one event handler does not break another event handler.
- **Native Event Syntax**: Every event supports `evt.Raised += handler`, and a source generator can add `producer.UserCreated += handler` to your classes.
- **Blazor Support**: The IntercomEventing.Blazor package ties subscriptions to a component's lifetime on Blazor Server and WebAssembly.
- **Easily Disposable**: The library provides a way to dispose of events and their subscriptions, ensuring that your application remains clean and efficient.


## Usage
To use the IntercomEventing library, you will need to install it via NuGet. Once installed, you can include the library with a using statement `using IntercomEventing;`

Every event is made up of an `event` and an `event call`.

An `event` is an object that holds the subscriptions. It is created once, usually as a property or field of the class that raises it, and it is raised many times. An `event call` is created by the library every time the event is raised. It contains the data you passed in (the payload, usually a record), the metadata for that raise, and a `CancellationToken` that is cancelled if the raise is cancelled or the event is disposed.

An `event call` is different from the event itself because it contains the per invocation information, while the event itself is only created once and is raised multiple times. Every handler of one raise receives the same call, and a call never changes after it is created, so two raises never share data.

### Basic Eventing Example
An example event that is raised when a user is created

1. Define the payload and the event producer

```csharp
using IntercomEventing;

public sealed record UserCreated(string Name, string Email);

public sealed class UserRegistry : IAsyncDisposable
{
    public IntercomEvent<UserCreated> UserCreated { get; } = new();

    public async Task CreateAsync(string name, string email)
    {
        var user = new UserCreated(name, email);
        // ... store the user ...
        await UserCreated.RaiseAsync(user, caller: this);
    }

    //the registry created the event, so it disposes it
    public ValueTask DisposeAsync()
    {
        return UserCreated.DisposeAsync();
    }
}
```

2. Define the event handler

**NOTE**: The event handler is defined as an async method that takes an `event call` as a parameter. The call type comes from the event, so you never have to name it when subscribing.

```csharp
public sealed class WelcomeMailer
{
    public async Task OnUserCreatedAsync(EventCall<UserCreated> call)
    {
        var (user, metadata) = call;
        await Task.Delay(TimeSpan.FromMilliseconds(10), call.CancellationToken);
        Console.WriteLine($"Welcome mail queued for {user.Name} <{user.Email}> ({metadata.EventName} call {metadata.EventCallId})");
    }
}
```

3. Subscribe to the event, raise it and unsubscribe

```csharp
await using var registry = new UserRegistry();
var mailer = new WelcomeMailer();

registry.UserCreated.Raised += mailer.OnUserCreatedAsync;
await registry.CreateAsync("Ada", "ada@example.com");   // completes after the handler ran
registry.UserCreated.Raised -= mailer.OnUserCreatedAsync;
```

### Event Types
The library provides several types of events, including generic events, threshold events, state change events, timed events and debounced events.

Each type of event is designed to handle specific use cases and provide the necessary functionality. Every type of event also has a version without a payload, for example `IntercomEvent` and `TimedEvent`, whose calls use the `NoPayload` type.

The methods that raise an event (`RaiseAsync`, `IncrementAsync`, `ChangeStateAsync`, `Start` and `Trigger`) take an optional `caller`, which is stored in `Metadata.Caller`. Every event's constructor also takes optional `EventingOptions` (see [Eventing Options](#eventing-options)), and you can set a `Name` that is used in the metadata.

#### Generic Event
A generic event (`IntercomEvent<TPayload>`) is the basic event type. It is raised with `RaiseAsync(payload)`, and its handlers receive an `EventCall<TPayload>`.

A public event property can be raised by anyone that has access to it. To make sure only the producer can raise an event, keep the event in a private field and expose it through its subscribe-only view `IIntercomEvent<TCall>`, which lets subscribers subscribe but not raise or dispose the event.

```csharp
public sealed class UserRegistry : IAsyncDisposable
{
    //only the registry can raise UserRemoved: subscribers see it through the subscribe-only view below
    private readonly IntercomEvent<UserRemoved> _userRemoved = new();

    public IntercomEvent<UserCreated> UserCreated { get; } = new() { Name = "UserCreated" };

    public IIntercomEvent<EventCall<UserRemoved>> UserRemoved => _userRemoved;

    public IntercomEvent Cleared { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await UserCreated.DisposeAsync();
        await _userRemoved.DisposeAsync();
        await Cleared.DisposeAsync();
    }
}
```

#### Threshold Event
A threshold event is an event that tracks a value and raises an event when the value reaches or exceeds a threshold. The value is changed with `IncrementAsync(amount, payload)`, which returns whether that increment raised the event, and the call includes the `Threshold`, the `PreviousValue` and the `CurrentValue`.

By default the event is raised on every increment that leaves the value at or past the threshold (`ThresholdRaiseMode.EveryTimeReached`). With `ThresholdRaiseMode.OnlyWhenCrossed` it is only raised on the increment that crosses the threshold, and again once the value drops back below it or is reset.

```csharp
public sealed class LoginGuard : IAsyncDisposable
{
    private const int FailedLoginLimit = 3;

    public ThresholdEvent<int, LoginAttempt> TooManyFailedLogins { get; } = new(threshold: FailedLoginLimit, ThresholdRaiseMode.OnlyWhenCrossed);

    public Task<bool> RecordFailedLoginAsync(LoginAttempt attempt)
    {
        return TooManyFailedLogins.IncrementAsync(1, attempt, caller: this);
    }

    public void RecordSuccessfulLogin()
    {
        TooManyFailedLogins.Reset();
    }

    public ValueTask DisposeAsync()
    {
        return TooManyFailedLogins.DisposeAsync();
    }
}
```

`Reset()` sets the value back to zero and `Reset(startingValue)` sets it to any value, neither of them raises the event. For a value that counts down towards its threshold, pass a custom check and set a starting value.

example of a battery that warns when it runs low
```csharp
public sealed class BatteryMonitor : IAsyncDisposable
{
    private const int LowChargePercent = 20;
    private const int FullChargePercent = 100;

    public ThresholdEvent<int> LowBattery { get; } = new(threshold: LowChargePercent, isThresholdReached: (charge, limit) => charge <= limit);

    public BatteryMonitor()
    {
        //the running value starts at zero, which is already "low"; start from a full battery instead
        LowBattery.Reset(startingValue: FullChargePercent);
    }

    public Task<bool> DrainAsync(int percent)
    {
        return LowBattery.IncrementAsync(-percent, caller: this);
    }

    public ValueTask DisposeAsync()
    {
        return LowBattery.DisposeAsync();
    }
}
```

#### State Change Event
A state change event is an event that tracks a state. The event will only be raised when the state changes: `ChangeStateAsync` returns `false`, and raises nothing, when the new state is the same as the current one. The call includes the `OldState` and the `NewState`.

```csharp
public sealed class Server : IAsyncDisposable
{
    public string Name { get; }

    public StateChangeEvent<PowerState, PowerChangeRequest> PowerChanged { get; } = new(PowerState.Off);

    public Server(string name)
    {
        Name = name;
    }

    public Task<bool> SetPowerAsync(PowerState newState, string requestedBy)
    {
        return PowerChanged.ChangeStateAsync(newState, new PowerChangeRequest(requestedBy), caller: this);
    }

    public ValueTask DisposeAsync()
    {
        return PowerChanged.DisposeAsync();
    }
}
```

example handling the state change
```csharp
server.PowerChanged.Subscribe(call =>
{
    var entry = $"{server.Name}: {call.OldState} -> {call.NewState} (requested by {call.Payload.RequestedBy})";
    Console.WriteLine(entry);
});
```

#### Timed Event
A timed event is an event that is raised at a specified interval after `Start(payload)` is called. The event will be raised once, or on every tick until `Stop()` is called if auto reset is enabled. Calling `Start` again restarts the timer, which makes an idle timeout easy.

```csharp
public sealed class SessionTracker : IAsyncDisposable
{
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
```

`Stop()` can be called from inside a handler, the current tick still reaches every handler.

example stopping a heartbeat after three ticks
```csharp
await using var heartbeat = new TimedEvent(HeartbeatInterval);
heartbeat.Subscribe(call =>
{
    if (call.TickNumber == 3)
    {
        heartbeat.Stop();
    }
});
heartbeat.Start();
```

**NOTE**: The next tick waits until the handlers of the current tick are done (or have moved to the background), so ticks never overlap. The timer uses `EventingOptions.TimeProvider`, so tests can use a fake clock.

#### Debounce Event
A debounce event is an event that debounces calls to `Trigger`. The event will only be raised after the debounce interval has passed without any new calls to `Trigger`, and it carries the payload of the last trigger along with `CoalescedTriggerCount`, the number of triggers it covered. `FlushAsync` raises a pending trigger right away.

```csharp
public sealed class SearchBox : IAsyncDisposable
{
    public DebounceEvent<SearchQuery> SearchChanged { get; }

    public SearchBox(TimeSpan quietPeriod)
    {
        SearchChanged = new DebounceEvent<SearchQuery>(quietPeriod);
    }

    public void Type(string text)
    {
        SearchChanged.Trigger(new SearchQuery(text), caller: this);
    }

    public Task<bool> SubmitAsync()
    {
        return SearchChanged.FlushAsync();
    }

    public ValueTask DisposeAsync()
    {
        return SearchChanged.DisposeAsync();
    }
}
```

**NOTE**: `Trigger` never waits, the event is raised later in the background. Disposing the event drops a trigger that is still waiting.

#### Custom Event Types
To build your own type of event, inherit from `IntercomEventBase<TCall>` and raise it through the protected `DispatchAsync` method. Subscribing, exception handling, timeouts and disposal all come from the base class.

```csharp
public sealed class CounterEvent : IntercomEventBase<EventCall<int>>
{
    private int _count;

    public CounterEvent(EventingOptions? options = null)
        : base(options)
    {
    }

    public Task IncrementAsync(CancellationToken cancellationToken = default)
    {
        var newCount = Interlocked.Increment(ref _count);
        //the raise data goes into the call through the state argument, never through fields read later
        return DispatchAsync(newCount, static (count, metadata, token) => new EventCall<int>(count, metadata, token), caller: this, cancellationToken);
    }
}
```

**NOTE**: Background work that nobody awaits, like a timer loop, should be started with the protected `StartWithoutCallerContext` method. The method that starts it is often called from inside another event's handler, and the work would otherwise carry that handler's context (such as `Activity.Current` and logging scopes) into every later raise. The [sample applications](#sample-applications) include two custom event types, `ThrottleEvent<T>` and `BatchEvent<T>`.

### Subscribing
There are three ways to subscribe to an event:
1. **`evt.Raised += handler`**: The native .NET event syntax. It works on properties, readonly fields, interfaces and injected services, and `evt.Raised -= handler` removes the handler again.
2. **`evt.Subscribe(handler, onException)`**: Subscribes right away and returns a subscription handle. Dispose the handle to unsubscribe.
3. **`await evt.SubscribeAsync(handler, options)`**: Like `Subscribe`, but it also takes lifecycle hooks and an exception handler for this subscription.

`Raised` only accepts async handlers (`Func<TCall, Task>`), while `Subscribe` and `SubscribeAsync` also accept a synchronous `Action<TCall>`.

**NOTE**: `evt += handler` directly on the event does not compile. The event is exposed through a property, and C# does not allow `+=` on a get-only property, which is why every event has a `Raised` member. To get `producer.UserCreated += handler` instead, use the source generator (see [Generated Events](#generated-events)).

#### Subscription Handles and Hooks
A subscriber with its own lifetime can keep its subscription handles and dispose them when it is disposed.

```csharp
//in OrderDashboard : IAsyncDisposable
//handles of different events stored together through the untyped IEventSubscription
private readonly List<IEventSubscription> _subscriptions = [];

public async Task StartAsync(OrderBook orders)
{
    //Subscribe: registers at once and returns the handle; a synchronous handler binds to the Action overload
    _subscriptions.Add(orders.OrderPlaced.Subscribe(OnOrderPlaced));

    //SubscribeAsync: lifecycle hooks and a per-subscription exception handler; new() { ... } infers the call type
    var cancellationSubscription = await orders.OrderCancelled.SubscribeAsync(OnOrderCancelledAsync, new()
    {
        OnSubscribed = subscription => RecordLifecycleAsync($"subscribed to {subscription.Event.Name}"),
        OnUnsubscribed = subscription => RecordLifecycleAsync($"unsubscribed from {subscription.Event.Name}"),
        OnException = (exception, call) => RecordLifecycle($"could not cancel order {call.Payload.OrderNumber}: {exception.Message}"),
    });
    _subscriptions.Add(cancellationSubscription);
}

public async ValueTask DisposeAsync()
{
    foreach (var subscription in _subscriptions)
    {
        //DisposeAsync awaits the OnUnsubscribed hook; Dispose() would start it in the background
        await subscription.DisposeAsync();
    }
    _subscriptions.Clear();
}
```

The subscription options passed to `SubscribeAsync` have three optional members:
1. **OnSubscribed**: Runs before the handler is registered, and `SubscribeAsync` returns after it. If it throws, nothing is subscribed and the exception is thrown to the caller.
2. **OnUnsubscribed**: Runs once when the subscription is removed, whichever way it is removed.
3. **OnException**: Receives each exception thrown by this handler, together with the call. It is used instead of the default exception handler.

#### Unsubscribing
- `await handle.DisposeAsync()` removes the handler and waits for its `OnUnsubscribed` hook.
- `handle.Dispose()`, `evt.Raised -= handler` and `evt.Unsubscribe(handler)` remove the handler right away and run the hook in the background, so they never block.
- Disposing the event removes every subscription and waits for all of their hooks.

**NOTE**: `-=` removes the most recently added subscription of an equal delegate. A lambda written a second time is a different delegate, so keep the delegate in a variable or use a method group. A subscription that is never removed lives as long as the event, the same as with a native .NET event.

#### One Handler for Many Events
Every call type inherits from `EventCall`, so a handler that takes an `EventCall` can be attached to any event.

```csharp
//in AuditLog
public Task RecordAsync(EventCall call)
{
    var entry = $"{call.Metadata.EventName} call {call.Metadata.EventCallId} at {call.Metadata.RaisedAt:HH:mm:ss.fff}";
    _entries.Enqueue(entry);
    Console.WriteLine($"Audit: {entry}");
    return Task.CompletedTask;
}
```

```csharp
orders.OrderPlaced.Raised += auditLog.RecordAsync;
orders.OrderCancelled.Raised += auditLog.RecordAsync;
```

### Generated Events
Mark an event field in a `partial` class with `[IntercomEvent]`, and the source generator adds two public members for it:
1. A native .NET event with the event's name, so subscribers can write `room.UserJoined += handler`.
2. A `<Name>Event` property that returns the event as its subscribe-only view, for `Subscribe` and `SubscribeAsync`.

```csharp
public sealed partial class ChatRoom : IAsyncDisposable
{
    [IntercomEvent]
    private readonly IntercomEvent<UserJoined> _userJoinedEvent = new();

    [IntercomEvent]
    private readonly IntercomEvent<MessagePosted> _messagePosted = new();

    [IntercomEvent(Name = "Closed")]
    private readonly IntercomEvent _closing = new();

    public Task JoinAsync(string name)
    {
        return _userJoinedEvent.RaiseAsync(new UserJoined(name), caller: this);
    }

    //the generated members expose the fields without a way to dispose them, so the room disposes its own events
    public async ValueTask DisposeAsync()
    {
        await _userJoinedEvent.DisposeAsync();
        await _messagePosted.DisposeAsync();
        await _closing.DisposeAsync();
    }
}
```

example subscribing through the generated members
```csharp
room.UserJoined += transcript.OnUserJoinedAsync;
room.Closed += transcript.OnRoomClosedAsync;
//the view property gives a disposable handle; Subscribe accepts a synchronous handler too
var messageSubscription = room.MessagePostedEvent.Subscribe(transcript.OnMessagePosted);
```

With a `private readonly` field, subscribers get the normal event syntax without a way to raise or dispose the event.

The event name comes from the field name: a leading `_` (or `m_` / `s_`) and a trailing `Event` are removed and the first letter is upper-cased, so `_userJoinedEvent` becomes `UserJoined` with a `UserJoinedEvent` property. Use `[IntercomEvent(Name = "Closed")]` to choose the name yourself.

**NOTE**: The field's type must be an event type, and the class (and any class it is nested in) must be `partial`. Structs and records work too, and a `static` field generates `static` members. The generator reports problems as compiler diagnostics:

| Diagnostic | Severity | Cause |
|---|---|---|
| INTEV001 | Error | The class, or a class it is nested in, is not `partial`. |
| INTEV002 | Error | The field is in an interface, enum or file-local type. |
| INTEV003 | Error | The field's type is not an event type. |
| INTEV004 | Error | The field's type is nullable. |
| INTEV005 | Warning | The field is not `readonly`, so assigning a new event would lose the existing subscribers. |
| INTEV006 | Error | The event name is not a valid C# identifier, or is a keyword. |
| INTEV007 | Error | A generated member name is already used in the class. |
| INTEV008 | Error | Two fields in one class generate the same member name. |
| INTEV009 | Error | The payload is less accessible than the generated members. |
| INTEV010 | Error | The field is `const`. |
| INTEV011 | Error | `[field: IntercomEvent]` is used on a property, event or parameter instead of a field. |
| INTEV012 | Error | The payload comes from an assembly referenced only through an `extern alias`; add `global` to its aliases. |

A diagnostic can be configured in `.editorconfig`, for example `dotnet_diagnostic.INTEV005.severity = none`, or with `#pragma warning disable`.

### Handling Exceptions
An event handler might throw an exception, typically with classic .NET events, these exceptions cannot be caught or handled outside the event handler.

With the IntercomEventing library, an exception thrown by a handler never reaches the code that raised the event, and one failing handler does not stop the others. The exception is handled by the first of these:
1. An exception handler provided when subscribing to the event (`onException` for `Subscribe`, `OnException` for `SubscribeAsync`).
2. The `DefaultExceptionHandler` in the event's `EventingOptions`, which receives an `EventHandlerFailure`.
3. If neither is set, the exception is suppressed and not propagated to the caller.

```csharp
var options = new EventingOptions
{
    DefaultExceptionHandler = failure =>
    {
        Console.WriteLine($"DefaultExceptionHandler: {failure.EventName} {failure.Stage} failed: {failure.Exception.Message}");
    },
};
await using var paymentReceived = new IntercomEvent<PaymentReceived>(options);

//handles its own failures
paymentReceived.Subscribe(
    call => throw new InvalidOperationException("receipt printer is offline"),
    onException: (exception, call) =>
    {
        Console.WriteLine($"Receipt printer handled its own failure for {call.Payload.InvoiceNumber}: {exception.Message}");
    });

//no exception handler of its own: the failure goes to DefaultExceptionHandler
paymentReceived.Raised += async call =>
{
    await Task.Yield();
    throw new TimeoutException("customer notification timed out");
};

await paymentReceived.RaiseAsync(new PaymentReceived("INV-1001", 120m));   //completes normally
```

An `EventHandlerFailure` contains the `Exception`, the `Stage` it happened in, the `EventName`, the `SubscriptionId` and the `Call`. Besides handler exceptions, the default exception handler also receives failures that nobody is waiting on, for example a failing `OnUnsubscribed` hook started by `-=`, or a failure in the background work of a timed or debounce event.

**NOTE**: A handler that throws `OperationCanceledException` after its `call.CancellationToken` was cancelled counts as cancelled, not failed, and is not reported.

### Eventing Options
Each event can be configured with `EventingOptions` passed to its constructor. Events created without their own options use `EventingOptions.Default`, which can be set with `AddIntercomEventing` (see [Dependency Injection](#dependency-injection)).
1. **DispatchMode**: Controls whether the event handlers of one raise are executed in parallel or sequentially. The default value is `DispatchMode.Parallel`.
2. **MaxConcurrentHandlers**: The maximum number of event handlers of one raise that can be executed at the same time in parallel mode. The default value is 100.
3. **StartNextEventHandlerAfter**: How long to wait for an event handler before starting the next one. The default value is 1 second. The current event handler will continue to run in the background, and `null` (or `Timeout.InfiniteTimeSpan`) waits for every handler. Other negative values become zero and values over 49 days become 49 days.
4. **AllowMultipleSubscribers**: If true, then more than one subscriber can subscribe to the event, otherwise a second subscription throws. The default value is true.
5. **DefaultExceptionHandler**: Receives the exceptions that no subscription handles itself.
6. **TimeProvider**: The clock used for timestamps, handler timeouts, timers and debounce intervals. It can be replaced with a fake clock in tests.

```csharp
var importCompleted = new IntercomEvent<ImportCompleted>(new EventingOptions { DispatchMode = DispatchMode.Sequential });

var reportRequested = new IntercomEvent<ReportRequested>(new EventingOptions
{
    DispatchMode = DispatchMode.Sequential,
    StartNextEventHandlerAfter = SlowHandlerTimeout,
});
```

**NOTE**: Options passed to an event replace the defaults completely. To change only one setting, start from the default: `EventingOptions.Default with { DispatchMode = DispatchMode.Sequential }`.

### Handler Timeout
By default, the system waits up to one second for an event handler before it moves on. The slow handler keeps running in the background, its exceptions are still handled, and disposing the event waits for it to finish. `RaiseAsync` completes once every handler has finished or has been moved to the background.

The timeout can be customized with the `StartNextEventHandlerAfter` option.

`An event is raised twice`
```csharp
await registry.CreateAsync("Ada", "ada@example.com");
await registry.CreateAsync("Grace", "grace@example.com");
```

`The event handler takes 5 seconds, but each raise only waits 1 second for it`
```csharp
public async Task OnUserCreatedAsync(EventCall<UserCreated> call)
{
    Console.WriteLine($"Event call for {call.Payload.Name} started at {DateTime.Now:HH:mm:ss}");
    await Task.Delay(TimeSpan.FromSeconds(5), call.CancellationToken);
    Console.WriteLine($"Event call for {call.Payload.Name} ended at {DateTime.Now:HH:mm:ss}");
}
```

The above code would print
```
Event call for Ada started at 12:00:00
Event call for Grace started at 12:00:01
Event call for Ada ended at 12:00:05
Event call for Grace ended at 12:00:06
```

**NOTE**: The timeout only covers the asynchronous part of a handler, synchronous work before the handler's first `await` is not interrupted. For busy events whose handlers always finish quickly, `StartNextEventHandlerAfter = null` avoids the small cost of tracking each handler.

### Disposal
Events implement `IAsyncDisposable`, and the class that creates an event should dispose it. Disposing an event cancels `call.CancellationToken` for running handlers, stops its timer or pending debounce, removes every subscription, and waits for handlers that were moved to the background and for the `OnUnsubscribed` hooks. Disposing more than once is safe, and raising or subscribing to a disposed event throws `ObjectDisposedException`.

**NOTE**: Events only implement `IAsyncDisposable`, so a DI container that holds an event has to be disposed with `DisposeAsync`. The Generic Host, ASP.NET Core and Blazor already do that. In a console app or a test, use `await using var provider = services.BuildServiceProvider();`.

### Dependency Injection
`AddIntercomEventing` sets `EventingOptions.Default` and registers `EventingOptions` as a singleton. It is in the `Microsoft.Extensions.DependencyInjection` namespace.

To build the options from your app's services, for example to log handler failures through an `ILogger`, pass a factory. The factory runs once when the host starts, before any hosted service's `StartAsync`.

```csharp
var builder = Host.CreateApplicationBuilder();
builder.Services.AddIntercomEventing(services =>
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IntercomEventing");
    return new EventingOptions
    {
        DispatchMode = DispatchMode.Parallel,
        MaxConcurrentHandlers = 32,
        StartNextEventHandlerAfter = TimeSpan.FromSeconds(5),
        DefaultExceptionHandler = failure => logger.LogError(failure.Exception, "{EventName} {Stage} failed", failure.EventName, failure.Stage),
    };
});
builder.Services.AddSingleton<ShippingService>();
builder.Services.AddSingleton<CustomerNotifier>();
builder.Services.AddHostedService(services => services.GetRequiredService<CustomerNotifier>());
```

Options that do not need any services can be passed directly.

```csharp
builder.Services.AddIntercomEventing(new EventingOptions { DispatchMode = DispatchMode.Sequential });
```

A hosted service can own a subscription for the lifetime of the app.

```csharp
public Task StartAsync(CancellationToken cancellationToken)
{
    _subscription = _shipping.OrderShipped.Subscribe(OnOrderShippedAsync);
    return Task.CompletedTask;
}

public async Task StopAsync(CancellationToken cancellationToken)
{
    if (_subscription is not null)
    {
        await _subscription.DisposeAsync();
    }
}
```

**NOTE**: `EventingOptions.Default` is shared by the whole process. Calling `AddIntercomEventing` again with the same options does nothing, but different options throw `InvalidOperationException`, so when one process runs several hosts at the same time (parallel integration tests, for example), give their events their own `EventingOptions`. Without a host, such as Blazor WebAssembly or a provider from `BuildServiceProvider`, call `provider.GetRequiredService<EventingOptions>()` once after building it so the factory runs.


## Blazor
The IntercomEventing.Blazor package ties subscriptions to the lifetime of a component, on Blazor Server and WebAssembly.
- Handlers run on the component's renderer, wherever the event was raised, and the component re-renders afterwards (pass `renderAfterHandler: false` to skip that).
- Every subscription is removed when the component is disposed.
- Nothing is subscribed while a component is prerendered, the interactive component subscribes when it initializes.
- Handler exceptions reach the nearest `ErrorBoundary`.

Add both namespaces to `_Imports.razor`

```razor
@using IntercomEventing
@using IntercomEventing.Blazor
```

### IntercomComponentBase
Inherit from `IntercomComponentBase` and subscribe through `Events` in `OnInitialized` or `OnInitializedAsync`.

```razor
@* UserFeed.razor *@
@inherits IntercomComponentBase
<ul>
    @foreach (var name in RecentNames)
    {
        <li>@name</li>
    }
</ul>
```

```csharp
// UserFeed.razor.cs
public partial class UserFeed
{
    private readonly List<string> _recentNames = [];

    [Inject]
    private UserDirectory Users { get; set; } = default!;

    protected IReadOnlyList<string> RecentNames => _recentNames;

    protected override void OnInitialized()
    {
        //runs on the renderer, re-renders afterwards, and is removed when the component is disposed
        Events.Subscribe(Users.UserJoined, call => _recentNames.Insert(0, call.Payload.Name));
    }
}
```

**NOTE**: For your own cleanup, override `DisposeAsyncCore` or implement `IDisposable`, both run after the subscriptions are removed. Do not declare `DisposeAsync` in the derived component.

### ComponentEventSubscriptions
When a component already has a base class, create a `ComponentEventSubscriptions` yourself and dispose it with the component. For an event that is passed in as a parameter or cascading value, call `Resubscribe` in `OnParametersSet` to keep one subscription on whichever event is current.

```csharp
public partial class OrderTicker : IAsyncDisposable
{
    private readonly ComponentEventSubscriptions _events;
    private EventSubscription<EventCall<OrderPlaced>>? _orderSubscription;

    //the subscribe-only view: a cascaded IntercomEvent<OrderPlaced> matches it because it is assignable to this type
    [CascadingParameter]
    public IIntercomEvent<EventCall<OrderPlaced>>? OrderPlaced { get; set; }

    protected int OrderCount { get; private set; }

    protected decimal LastTotal { get; private set; }

    public OrderTicker()
    {
        //InvokeAsync, StateHasChanged, DispatchExceptionAsync and RendererInfo are protected, so the component hands them over
        _events = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
    }

    public ValueTask DisposeAsync()
    {
        return _events.DisposeAsync();
    }

    protected override void OnParametersSet()
    {
        //a cascading value can change between renders; this keeps one subscription on whichever event is current
        _orderSubscription = _events.Resubscribe(_orderSubscription, OrderPlaced, OnOrderPlaced);
    }

    private void OnOrderPlaced(EventCall<OrderPlaced> call)
    {
        OrderCount++;
        LastTotal = call.Payload.Total;
    }
}
```

example cascading an event registered in DI with `AddCascadingValue`
```csharp
services.AddScoped(_ => new IntercomEvent<OrderPlaced>());
services.AddCascadingValue(serviceProvider => serviceProvider.GetRequiredService<IntercomEvent<OrderPlaced>>());
```

**NOTE**: Choose the lifetime of an event on purpose. On Blazor Server a singleton event is shared by every user's circuit, while a scoped one lives for one circuit. On WebAssembly both mean one per browser tab.

### EventListener
`<EventListener>` subscribes from markup while it is rendered, and renders nothing itself. `OnRaised` is an `EventCallback`, so the component re-renders without calling `StateHasChanged`.

```razor
@if (ShowListeners)
{
    <EventListener TCall="EventCall<OrderPlaced>" Event="OrderEvent" OnRaised="OnOrderPlaced" />
    <EventListener Event="OrderEvent" OnRaised="(EventCall<OrderPlaced> call) => AddToTotal(call)" />
}
```

**NOTE**: Razor cannot work out `TCall` from a method group, so name it (`TCall="EventCall<OrderPlaced>"`) or use a lambda with a typed parameter. Changing `Event` moves the subscription to the new event, and `null` unsubscribes.

### Errors
A handler exception goes to the subscription's own exception handler if one is given, otherwise it goes to the nearest `ErrorBoundary`.

```razor
<ErrorBoundary>
    <ChildContent>
        <FailingSubscriber Source="Source" HandleInComponent="HandleInComponent" />
    </ChildContent>
    <ErrorContent Context="error">
        <p class="boundary">boundary: @error.Message</p>
    </ErrorContent>
</ErrorBoundary>
```

```csharp
Events.Subscribe(Source, ThrowForCall, onException: (exception, call) => HandledMessage = $"handled: {exception.Message}");
```

Without an `ErrorBoundary` the exception is unhandled, just like an exception from a button click handler: it ends a Blazor Server circuit and shows the error UI on WebAssembly. A handler whose task ends cancelled is ignored, the same way Blazor ignores a cancelled UI event handler.


## Things to Watch Out For
- **Async void handlers**: An `async void` method can be passed to `Subscribe` as an `Action`, and its exceptions then skip the exception handlers and can crash the process (or end a Blazor Server circuit). Always give async handlers a `Task` return type.
- **Raises are not serialized**: When an event is raised several times at once, the raises are delivered at the same time and their handlers can interleave. Await each raise when the order matters.
- **A raise from inside a handler can arrive first**: When a handler raises another event (a reply, for example), the reply can reach the other subscribers before the original call does, even when every raise is awaited. When the order matters, raise the follow-up after the original `RaiseAsync` has returned, or order the calls by `Metadata.RaisedAt`.
- **Shared default options**: `EventingOptions.Default` is shared by the whole process, so give an event its own options when it must not follow the default.


## Migrating from 1.x
2.0 is a redesign, so most 1.x code changes shape rather than names.

| 1.x | 2.0 |
|---|---|
| `using IntercomEventing.Features.Events;` | `using IntercomEventing;` |
| `record XEvent : GenericEvent<XEvent>`, `record XEventCall : EventCall<XEvent>` and a `CreateEventCall(params object[])` override | `IntercomEvent<X>` with a payload record `X`, no event or call class of your own |
| Raise data kept in private fields of the event | The payload argument of the raise method |
| `await evt.RaiseEvent(call, eventCaller)` | `await evt.RaiseAsync(payload, caller: this)` |
| Handler `Task Handle(XEventCall call)` reading `call.User` | Handler `Task Handle(EventCall<User> call)` reading `call.Payload` |
| `await evt.Subscribe<XEventCall>(handler, onSubscribe, onUnsubscribe, exceptionHandler)` | `evt.Raised += handler`, `evt.Subscribe(handler, onException)` or `await evt.SubscribeAsync(handler, new() { OnSubscribed, OnUnsubscribed, OnException })` |
| `Action<Exception>` exception handler | `Action<Exception, TCall> onException` |
| `evt.Unsubscribe(subscription)`, blocking `subscription.Dispose()` | `await subscription.DisposeAsync()`, non-blocking `subscription.Dispose()`, `evt.Raised -= handler` |
| `Metadata.EventCaller` | `Metadata.Caller` |
| `Metadata.LastEventTime` (`DateTime`) | `Metadata.RaisedAt` (`DateTimeOffset`) |
| `ThresholdEvent<TEvent, TValue>`, `IncrementValue(amount)` | `ThresholdEvent<TValue, TPayload>` or `ThresholdEvent<TValue>`, `await IncrementAsync(amount, payload)` |
| `StateChangeEvent<TEvent, TState>`, `CurrentState = value` | `StateChangeEvent<TState, TPayload>` or `StateChangeEvent<TState>`, `await ChangeStateAsync(value, payload)` |
| `TimedEvent<TEvent>`, `Start()`, `Stop()` | `TimedEvent<TPayload>` or `TimedEvent`, `Start(payload)`, `Stop()` |
| `DebounceEvent<TEvent>`, protected `StartDebounce()` | `DebounceEvent<TPayload>` or `DebounceEvent`, `Trigger(payload)`, `FlushAsync()` |
| `AddIntercomEventing(options => { ... })` | `AddIntercomEventing(new EventingOptions { ... })`, a factory, or options per event |
| `AddIntercomEventing` in the `IntercomEventing` namespace | `AddIntercomEventing` in `Microsoft.Extensions.DependencyInjection` |
| `EventingConfiguration` injected from DI | `EventingOptions` injected from DI, or `EventingOptions.Default` |
| `evt.IsSubscribed(subscription)` | `subscription.IsActive` |
| `subscription.SubCancelToken` | `call.CancellationToken` in the handler |
| `EventingOptions.SyncType` (`EventingSyncType`) | `EventingOptions.DispatchMode` (`DispatchMode`) |
| `MaxNumberOfConcurrentHandlers` | `MaxConcurrentHandlers` |
| `DefaultExceptionHandler` as `Action<Exception>` | `DefaultExceptionHandler` as `Action<EventHandlerFailure>` |
| `StartNextEventHandlerAfter` as `TimeSpan` | `TimeSpan?`, where `null` waits for every handler |
| Events are records | Events are classes |
| `net9.0` and `net10.0`, with package dependencies | `net11.0`, with no package dependencies |

Behavior that changed along the way:
- Exceptions of async handlers that fail before the timeout are now handled, 1.x lost them.
- Disposing a subscription removes it, and nothing blocks or runs from a finalizer.
- Raising an event, subscribing and unsubscribing are safe from several threads at once, and each raise has its own call.
- Options belong to each event (or to `EventingOptions.Default`) instead of static configuration.


## Sample Applications
The [`Samples`](https://github.com/DragoQCC/IntercomEventing/tree/master/Samples) folder has two small apps that use the library the way an application would. They reference the projects in this repository, your own app installs the packages instead.

```shell
dotnet run --project Samples/IntercomEventing.Samples.BlazorServer   # http://localhost:5280
dotnet run --project Samples/IntercomEventing.Samples.WebApi         # http://localhost:5281
```

**Blazor Server** (`IntercomEventing.Samples.BlazorServer`): Open it in two browser tabs, its singleton services share their events with every tab.
- **Server rack**: A `StateChangeEvent` on a singleton, handled through `IntercomComponentBase` in every tab.
- **Search**: A `DebounceEvent` on a scoped (per tab) service, with `FlushAsync` on Enter.
- **Chat**: `[IntercomEvent]` members, `<EventListener>`, a hosted service subscribed with `+=`, and a failing handler caught by an `ErrorBoundary`.
- **Telemetry**: A `TimedEvent`, a `ThresholdEvent`, the custom `ThrottleEvent<T>`, and `Resubscribe` on a cascaded event.

**Minimal API** (`IntercomEventing.Samples.WebApi`): `IntercomEventing.Samples.WebApi.http` has a request for each endpoint, and `curl -N http://localhost:5281/events/stream` shows the events as they are raised.
- **Orders**: An `IntercomEvent` behind its subscribe-only view, a hosted subscriber with `SubscribeAsync` hooks, a `ThresholdEvent` sales goal, and handler failures logged through `DefaultExceptionHandler`.
- **Store**: A `StateChangeEvent`, orders are rejected while the store is not open.
- **Catalog**: A `DebounceEvent` that republishes the price list once per burst of price changes.
- **Audit**: One `EventCall` handler on every event, and the custom `BatchEvent<T>` flushed by a `TimedEvent`.
- **Live feed**: Server-Sent Events, with one subscription per connected client that is removed when the client disconnects.


## Examples and Benchmarks
The [`IntercomEventing.Benchmark`](https://github.com/DragoQCC/IntercomEventing/tree/master/IntercomEventing.Benchmark) project has self-checking examples, one folder per topic, and benchmarks that compare the library with a classic `event Func<T, Task>`.

```shell
dotnet run -c Release --project IntercomEventing.Benchmark -- examples              # every example
dotnet run -c Release --project IntercomEventing.Benchmark -- examples Debounce     # one example
dotnet run -c Release --project IntercomEventing.Benchmark -- benchmarks            # every benchmark
dotnet run -c Release --project IntercomEventing.Benchmark -- benchmarks --job Dry  # quick smoke run
```

The examples are `Generic`, `Threshold`, `StateChange`, `Timed`, `Debounce`, `GeneratedEvents`, `Subscriptions`, `ErrorHandling`, `Dispatch` and `DependencyInjection`. The benchmarks measure raising an event with 0, 1, 10 and 100 subscribers, and the cost of subscribing and unsubscribing. Arguments after `benchmarks` go to BenchmarkDotNet, for example `--job Short` or `--filter *Subscription*`.

**NOTE**: The benchmarks run in process, because the BenchmarkDotNet version used does not recognise .NET 11 yet.


## License
This library is licensed under the MIT License. See the [LICENSE](https://github.com/DragoQCC/IntercomEventing/blob/master/LICENSE) file for more information.
