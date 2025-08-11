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

    protected override InputChangedEventCall CreateEventCall(params object[]? args) => new(_lastInput ?? throw new InvalidOperationException("No input available"));
}

public record InputChangedEventCall(UserInput Input) : DebounceEventCall<InputChangedEvent>;