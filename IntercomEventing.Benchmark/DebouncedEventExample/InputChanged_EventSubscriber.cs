namespace IntercomEventing.Benchmark.DebouncedEventExample;

public class InputChanged_EventSubscriber
{
    public async Task HandleInputChangedEventAsync(InputChangedEventCall eventCall)
    {
        string callTime = eventCall.Metadata.LastEventTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string inputTime = eventCall.Input.InputTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        
        Console.WriteLine($"Input changed event call {eventCall.Metadata.EventCallId} handled at {callTime}");
        Console.WriteLine($"User input: '{eventCall.Input.Text}' (entered at {inputTime})");
        Console.WriteLine($"Debounce interval: {eventCall.DebounceInterval.TotalMilliseconds}ms");
        Console.WriteLine($"Time between input and processing: {(eventCall.Metadata.LastEventTime - eventCall.Input.InputTime).TotalMilliseconds}ms");
        Console.WriteLine("---");
        
        await Task.Delay(100); // Simulate some work
    }
}