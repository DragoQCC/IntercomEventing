using IntercomEventing.Benchmark.DebouncedEventExample;

namespace IntercomEventing.Tests;

public class DebounceEventTests
{
    [Test]
    public async Task DebouncedEvent_ShouldOnlyTriggerOnceForMultipleRapidInputs()
    {
        // Arrange
        var inputEventProducer = new InputChanged_EventProducer();
        int handlerCallCount = 0;
        string? lastProcessedInput = null;
        var completionSource = new TaskCompletionSource<bool>();

        await inputEventProducer.InputChangedEvent.Subscribe<InputChangedEventCall>(async eventCall =>
        {
            handlerCallCount++;
            lastProcessedInput = eventCall.Input.Text;
            completionSource.SetResult(true);
            await Task.CompletedTask;
        });

        // Act
        // Simulate rapid typing of "Hello"
        await inputEventProducer.SimulateUserInput("H");
        await Task.Delay(100);
        await inputEventProducer.SimulateUserInput("He");
        await Task.Delay(100);
        await inputEventProducer.SimulateUserInput("Hel");
        await Task.Delay(100);
        await inputEventProducer.SimulateUserInput("Hell");
        await Task.Delay(100);
        await inputEventProducer.SimulateUserInput("Hello");

        // Wait for the event to be processed
        await Task.WhenAny(completionSource.Task, Task.Delay(1000));

        // Assert
        await Assert.That(handlerCallCount).IsEqualTo(1);
        await Assert.That(lastProcessedInput).IsEqualTo("Hello");
    }
    
     [Test]
    public async Task DebouncedEvent_ShouldMaintainCorrectTiming()
    {
        // Arrange
        var inputEventProducer = new InputChanged_EventProducer();
        DateTime? eventHandleTime = null;
        DateTime? inputTime = null;
        var completionSource = new TaskCompletionSource<bool>();

        await inputEventProducer.InputChangedEvent.Subscribe<InputChangedEventCall>(async eventCall =>
        {
            eventHandleTime = eventCall.Metadata.LastEventTime;
            inputTime = eventCall.Input.InputTime;
            completionSource.SetResult(true);
            await Task.CompletedTask;
        });

        // Act
        await inputEventProducer.SimulateUserInput("Test Input");
        
        // Wait for the event to be processed
        await Task.WhenAny(completionSource.Task, Task.Delay(1000));

        // Assert
        await Assert.That(eventHandleTime).IsNotNull();
        await Assert.That(inputTime).IsNotNull();
        
        var timeDifference = eventHandleTime!.Value - inputTime!.Value;
        // Should be at least the debounce period (500ms)
        await Assert.That(timeDifference.TotalMilliseconds).IsGreaterThanOrEqualTo(500);
        // But not significantly more (giving 200ms buffer for processing)
        await Assert.That(timeDifference.TotalMilliseconds).IsLessThanOrEqualTo(700);
    }

    [Test]
    public async Task DebouncedEvent_ShouldNotTriggerBeforeDebounceInterval()
    {
        // Arrange
        var inputEventProducer = new InputChanged_EventProducer();
        int handlerCallCount = 0;

        await inputEventProducer.InputChangedEvent.Subscribe<InputChangedEventCall>(async _ =>
        {
            handlerCallCount++;
            await Task.CompletedTask;
        });

        // Act
        await inputEventProducer.SimulateUserInput("First Input");
        // Wait less than debounce period
        await Task.Delay(200);
        
        // Assert
        await Assert.That(handlerCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task DebouncedEvent_MultipleIterations_ShouldOnlyTriggerOncePerIteration()
    {
        // Arrange
        const int iterations = 100;
        var results = new List<(int iteration, int callCount)>();
        
        for (int i = 0; i < iterations; i++)
        {
            var inputEventProducer = new InputChanged_EventProducer();
            int handlerCallCount = 0;
            var completionSource = new TaskCompletionSource<bool>();

            // Subscribe to the event
            await inputEventProducer.InputChangedEvent.Subscribe<InputChangedEventCall>(async eventCall =>
            {
                handlerCallCount++;
                completionSource.SetResult(true);
                await Task.CompletedTask;
            });

            // Act
            // Simulate multiple rapid inputs
            for (int j = 0; j < 5; j++)
            {
                await inputEventProducer.SimulateUserInput($"Input {j}");
                await Task.Delay(50); // Very rapid inputs
            }

            // Wait for the debounce period plus a small buffer
            await Task.WhenAny(completionSource.Task, Task.Delay(1000));

            // Store the results for this iteration
            results.Add((i, handlerCallCount));

            // Small delay between iterations to ensure clean separation
            await Task.Delay(100);
        }

        // Assert
        // Check each iteration had exactly one call
        foreach (var (iteration, callCount) in results)
        {
            await Assert.That(callCount).IsEqualTo(1);
        }

        // Additional assertions to ensure overall behavior
        await Assert.That(results.Count).IsEqualTo(iterations);
        await Assert.That(results.Sum(r => r.callCount)).IsEqualTo(iterations);
    }
}