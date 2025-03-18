namespace IntercomEventing.Benchmark.DebouncedEventExample;

public class InputChanged_EventProducer
{
    public InputChangedEvent InputChangedEvent { get; init; } = new();
    
    public async Task SimulateUserInput(string text)
    {
        var input = new UserInput(text, DateTime.UtcNow);
        await InputChangedEvent.NotifyInputChanged(input);
    }
}