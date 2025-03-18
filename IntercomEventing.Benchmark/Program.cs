using System.Diagnostics;
using BenchmarkDotNet.Running;
using HelpfulTypesAndExtensions;
using IntercomEventing.Benchmark.DebouncedEventExample;
using IntercomEventing.Benchmark.StateChangedEventExample;
using IntercomEventing.Benchmark.ThresholdEventExample;
using IntercomEventing.Benchmark.TimedEventExample;
using IntercomEventing.Benchmark.UserCreationExample;
using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark;

class Program
{
    public static int NUM_SUBSCRIBERS {get;} = 10;
    public static int NUM_EVENT_EXEC {get;} = 10;
    static Stopwatch stopwatch = Stopwatch.StartNew();
    
    static async Task Main(string[] args)
    {
        //BenchmarkRunner.Run<EventBenchmarks>();
        //await CreateAndRunEvents();
        //await CreateUserCreatedEvent();
        //await CreateTimedEventForUserLogonSession();
        //await CreateServerPowerStateChangedEvent();
        
        //clear the console and run this method 20 times
        for (int i = 0; i < 20; i++)
        {
            Console.Clear();
            await CreateDebouncedInputEvent();
        }
        
    }

    public static async Task CreateAndRunEvents()
    {
        Console.WriteLine("Running benchmarks...");
        Console.WriteLine("\nExecuting healthy event handlers...");
        await CreateHealthyEvents();
        Console.WriteLine("Waiting 5 seconds for the healthy event handlers to complete...");
        await Task.Delay(5000);
        Console.WriteLine("\nExecuting faulty event handlers that randomly throw exceptions...");
        await CreateFaultyEvents();
        Console.WriteLine("\nExecuting long running event handlers that take 5 seconds to complete...");
        await CreateLongRunningEvents();
        
        Console.WriteLine("\nHanging event handlers that never complete...");
        //add in a force exit after 2 minutes
        Task exitHangingWait = Task.Run(async () =>
        {
            await Task.Delay(1000 * 60 * 2);
        });
       Task hangingEvents = CreateHangingEvents();

       var finishedTask = await Task.WhenAny(exitHangingWait, hangingEvents);
       if(finishedTask == exitHangingWait)
       {
           //cancel the hanging events task
           hangingEvents.Dispose();
       }
       Console.WriteLine("Benchmark complete");
    }

    


    public static async Task CreateHealthyEvents()
    {
        stopwatch.Restart();
        Console.WriteLine($"\nCreating and subscribing {NUM_SUBSCRIBERS} subscribers to an intercom event");
        ThresholdReached_IntercomEventProducer thresholdReachedIntercomEventProducer = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            await thresholdReachedIntercomEventProducer.ThresholdReachedEvent.Subscribe<CounterThresholdReachedEventCall>(ThresholdReached_EventSubscriber.HealthyIntercomEventHandlerAsync);
        }
        var timeToSubscribeIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"Creating and subscribing {NUM_SUBSCRIBERS} subscribers to a classic event");
        ThresholdReached_ClassicEventProducer thresholdReachedClassicEventProducer = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            thresholdReachedClassicEventProducer.ThresholdReached += async (o,e) => await ThresholdReached_EventSubscriber.HealthyClassicEventHandlerAsync(o,e);
        }
        var timeToSubscribeClassic = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"\nTime to subscribe {NUM_SUBSCRIBERS} intercom event handlers -> {timeToSubscribeIntercom.Seconds}s:{timeToSubscribeIntercom.Milliseconds:D3}ms:{timeToSubscribeIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to subscribe {NUM_SUBSCRIBERS} classic event handlers -> {timeToSubscribeClassic.Seconds}s:{timeToSubscribeClassic.Milliseconds:D3}ms:{timeToSubscribeClassic.Microseconds:D3}us");
        Console.WriteLine($"\nRunning {NUM_EVENT_EXEC} healthy events for each type...");
        
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await thresholdReachedIntercomEventProducer.IncrementCountEvent();
        }
        var timeToRunIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await thresholdReachedClassicEventProducer.IncrementCountEvent();
        }
        var timeToRunClassic = stopwatch.Elapsed;
        
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} healthy intercom events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunIntercom.Seconds}s:{timeToRunIntercom.Milliseconds:D3}ms:{timeToRunIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} healthy classic events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunClassic.Seconds}s:{timeToRunClassic.Milliseconds:D3}ms:{timeToRunClassic.Microseconds:D3}us");
    }
    
    public static async Task CreateFaultyEvents()
    {
        stopwatch.Restart();
        Console.WriteLine($"\nCreating and subscribing {NUM_SUBSCRIBERS} subscribers to an intercom event");
        ThresholdReached_IntercomEventProducer intercomEventClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            await intercomEventClass.ThresholdReachedEvent.Subscribe<CounterThresholdReachedEventCall>(ThresholdReached_EventSubscriber.FaultyIntercomEventHandlerAsync);
        }
        var timeToSubscribeIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"Creating and subscribing {NUM_SUBSCRIBERS} subscribers to a classic event");
        ThresholdReached_ClassicEventProducer classicEventClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            classicEventClass.ThresholdReached += async (o,e) => await ThresholdReached_EventSubscriber.FaultyClassicEventHandlerAsync(o,e);
        }
        var timeToSubscribeClassic = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"\nTime to subscribe {NUM_SUBSCRIBERS} intercom event handlers -> {timeToSubscribeIntercom.Seconds}s:{timeToSubscribeIntercom.Milliseconds:D3}ms:{timeToSubscribeIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to subscribe {NUM_SUBSCRIBERS} classic event handlers -> {timeToSubscribeClassic.Seconds}s:{timeToSubscribeClassic.Milliseconds:D3}ms:{timeToSubscribeClassic.Microseconds:D3}us");
        Console.WriteLine($"\nRunning {NUM_EVENT_EXEC} faulty events for each type...\n");
        
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await intercomEventClass.IncrementCountEvent();
        }
        var timeToRunIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await classicEventClass.IncrementCountEvent();
        }
        var timeToRunClassic = stopwatch.Elapsed;
        
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} faulty intercom events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunIntercom.Seconds}s:{timeToRunIntercom.Milliseconds:D3}ms:{timeToRunIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} faulty classic events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunClassic.Seconds}s:{timeToRunClassic.Milliseconds:D3}ms:{timeToRunClassic.Microseconds:D3}us");
    }
    
    public static async Task CreateLongRunningEvents()
    {
        stopwatch.Restart();
        Console.WriteLine($"\nCreating and subscribing {NUM_SUBSCRIBERS} subscribers to an intercom event");
        ThresholdReached_IntercomEventProducer intercomEventClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            await intercomEventClass.ThresholdReachedEvent.Subscribe<CounterThresholdReachedEventCall>(ThresholdReached_EventSubscriber.LongRunningIntercomEventHandlerAsync);
        }
        var timeToSubscribeIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"Creating and subscribing {NUM_SUBSCRIBERS} subscribers to a classic event");
        ThresholdReached_ClassicEventProducer classicEventClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            classicEventClass.ThresholdReached += async (o,e) => await ThresholdReached_EventSubscriber.LongRunningClassicEventHandlerAsync(o,e);
        }
        var timeToSubscribeClassic = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"\nTime to subscribe {NUM_SUBSCRIBERS} intercom event handlers -> {timeToSubscribeIntercom.Seconds}s:{timeToSubscribeIntercom.Milliseconds:D3}ms:{timeToSubscribeIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to subscribe {NUM_SUBSCRIBERS} classic event handlers -> {timeToSubscribeClassic.Seconds}s:{timeToSubscribeClassic.Milliseconds:D3}ms:{timeToSubscribeClassic.Microseconds:D3}us");
        Console.WriteLine($"\nRunning {NUM_EVENT_EXEC} long running events for each type...\n");
        
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await intercomEventClass.IncrementCountEvent();
        }
        var timeToRunIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await classicEventClass.IncrementCountEvent();
        }
        var timeToRunClassic = stopwatch.Elapsed;

        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} long running intercom events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunIntercom.Seconds}s:{timeToRunIntercom.Milliseconds:D3}ms:{timeToRunIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} long running classic events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunClassic.Seconds}s:{timeToRunClassic.Milliseconds:D3}ms:{timeToRunClassic.Microseconds:D3}us");
    }
    
    public static async Task CreateHangingEvents()
    {
        Console.WriteLine($"\nCreating and subscribing {NUM_SUBSCRIBERS} subscribers to an intercom event");
        ThresholdReached_IntercomEventProducer intercomEventClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            await intercomEventClass.ThresholdReachedEvent.Subscribe<CounterThresholdReachedEventCall>(ThresholdReached_EventSubscriber.HangingIntercomEventHandlerAsync);
        }
        var timeToSubscribeIntercom = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"Creating and subscribing {NUM_SUBSCRIBERS} subscribers to a classic event");
        ThresholdReached_ClassicEventProducer classicEventClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            classicEventClass.ThresholdReached += async (o,e) => await ThresholdReached_EventSubscriber.HangingClassicEventHandlerAsync(o,e);
        }
        var timeToSubscribeClassic = stopwatch.Elapsed;
        stopwatch.Restart();
        
        
        Console.WriteLine($"\nTime to subscribe {NUM_SUBSCRIBERS} intercom event handlers -> {timeToSubscribeIntercom.Seconds}s:{timeToSubscribeIntercom.Milliseconds:D3}ms:{timeToSubscribeIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to subscribe {NUM_SUBSCRIBERS} classic event handlers -> {timeToSubscribeClassic.Seconds}s:{timeToSubscribeClassic.Milliseconds:D3}ms:{timeToSubscribeClassic.Microseconds:D3}us");
        
        Console.WriteLine($"\nRunning {NUM_EVENT_EXEC} hanging events for each type...\n");
        
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await intercomEventClass.IncrementCountEvent();
        }
        var timeToRunIntercom = stopwatch.Elapsed;
       stopwatch.Restart();
        
        for(int i = 0; i < NUM_EVENT_EXEC; i++)
        {
            await classicEventClass.IncrementCountEvent();
        }
        var timeToRunClassic = stopwatch.Elapsed;
        stopwatch.Restart();
        
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} hanging intercom events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunIntercom.Seconds}s:{timeToRunIntercom.Milliseconds:D3}ms:{timeToRunIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to run {NUM_EVENT_EXEC} hanging classic events for {NUM_SUBSCRIBERS} subscribers -> {timeToRunClassic.Seconds}s:{timeToRunClassic.Milliseconds:D3}ms:{timeToRunClassic.Microseconds:D3}us");
    }
    
    //subscribe to a user created event and invoke it 
    public static async Task CreateUserCreatedEvent()
    {
        DebugHelp.DebugWriteLine("\nCreating and subscribing to a user created event");
        UserCreation_EventProducer userCreationEventProducer = new();
        UserCreation_EventSubscriber userCreationEventSubscriber = new();
        await userCreationEventProducer.UserCreatedEvent.Subscribe<UserCreatedEventCall>(userCreationEventSubscriber.HandleUserCreatedEventAsync);
        
        User user1 = new("John Doe", "john.doe@example.com");
        User user2 = new("Jane Doe", "jane.doe@example.com");
        User user3 = new("John Smith", "john.smith@example.com");
        User user4 = new("Jane Smith", "jane.smith@example.com");
        
        await userCreationEventProducer.CreateUserAsync(user1);
        await userCreationEventProducer.CreateUserAsync(user2);
        await userCreationEventProducer.CreateUserAsync(user3);
        await userCreationEventProducer.CreateUserAsync(user4);
        DebugHelp.DebugWriteLine("\nWaiting 5 seconds for the user created event to be handled...");
        await Task.Delay(5000);
        
        DebugHelp.DebugWriteLine("\nCreating and subscribing to a user threshold reached event");
        await userCreationEventProducer.UserThresholdReachedEvent.Subscribe<UserThresholdReachedEventCall>(userCreationEventSubscriber.HandleUserThresholdReachedEventAsync);
        
        await userCreationEventProducer.NotifyUserThresholdReachedAsync(user1);
        await userCreationEventProducer.NotifyUserThresholdReachedAsync(user2);
        await userCreationEventProducer.NotifyUserThresholdReachedAsync(user3);
        await userCreationEventProducer.NotifyUserThresholdReachedAsync(user4);
        
        await Task.Delay(5000);
    }
    
    //subscribe and execute a timed event 
    public static async Task CreateTimedEventForUserLogonSession()
    {
        DebugHelp.DebugWriteLine("\nCreating and subscribing to a timed event for a user logon session");
        TimerPassed_EventProducer loggonSessionService = new();
        TimerPassed_EventSubscribers logonSessionExpiredHandler = new();
        
        await loggonSessionService.LogonSessionTimerEvent.Subscribe<LogonSessionTimerEventCall>(logonSessionExpiredHandler.HandleLogonSessionExpiredAsync);
        User user1 = new("John Doe", "john.doe@example.com");
        string callTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        DebugHelp.DebugWriteLine($"\nStarting a user logon session timer for {user1.Name} at {callTime}");
        await loggonSessionService.TrackUserLogonSession(user1);
        DebugHelp.DebugWriteLine("Waiting 10 seconds for the logon session timer to expire...");
        await Task.Delay(10000);
        //additional wait time to allow the event to be handled before the method exits
        await Task.Delay(5000);
    }
    
    //create and execute a server power state changed event
    public static async Task CreateServerPowerStateChangedEvent()
    {
        DebugHelp.DebugWriteLine("\nCreating and subscribing to a server power state changed event");
        ServerPowerService_EventProducer serverPowerService = new();
        ServerPowerState_EventSubscriber serverPowerStateHandler = new();
        
        await serverPowerService.ServerPowerChangedEvent.Subscribe<ServerPowerChangedEventCall>(serverPowerStateHandler.HandleServerPowerChangedEventAsync);
        Server server1 = new()
        {
            Name = "Server1",
            IpAddress = "192.168.1.1"
        };
        await serverPowerService.ChangeServerPowerState(server1, ServerPower.On);
        await Task.Delay(5000);
    }

    public static async Task CreateDebouncedInputEvent()
    {
        DebugHelp.DebugWriteLine("\nCreating and subscribing to a debounced input event \n");
        var inputEventProducer = new InputChanged_EventProducer();
        var inputEventSubscriber = new InputChanged_EventSubscriber();
        
        await inputEventProducer.InputChangedEvent.Subscribe<InputChangedEventCall>(inputEventSubscriber.HandleInputChangedEventAsync);

        // Simulate user typing "Hello World!" rapidly
        var inputs = new[]
        {
            "H",
            "He",
            "Hel",
            "Hell",
            "Hello",
            "Hello ",
            "Hello W",
            "Hello Wo",
            "Hello Wor",
            "Hello Worl",
            "Hello World",
            "Hello World!"
        };

        // Simulate rapid typing
        foreach (var input in inputs)
        {
            await inputEventProducer.SimulateUserInput(input);
            await Task.Delay(100); // 100ms between keystrokes
        }
        // Wait to ensure all debounced events are processed
        await Task.Delay(5000);
    }
}