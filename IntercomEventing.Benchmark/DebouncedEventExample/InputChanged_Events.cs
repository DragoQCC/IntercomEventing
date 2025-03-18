using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.DebouncedEventExample;

public record UserInput(string Text, DateTime InputTime);

public record InputChangedEvent() : DebounceEvent<InputChangedEvent>(TimeSpan.FromMilliseconds(500))
{
    private UserInput? _lastInput;

    public async Task NotifyInputChanged(UserInput input)
    {
        _lastInput = input;
        await StartDebounce();
    }

    protected override DebounceEventCall<InputChangedEvent> CreateDebounceEventCall(TimeSpan debounceInterval) 
        => new InputChangedEventCall(debounceInterval, _lastInput ?? throw new InvalidOperationException("No input available"));
}

public record InputChangedEventCall(TimeSpan DebounceInterval, UserInput Input) 
    : DebounceEventCall<InputChangedEvent>(DebounceInterval);