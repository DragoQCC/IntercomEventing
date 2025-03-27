using IntercomEventing.Features.Events;

namespace IntercomEventing.Tests;


public record MockTimedEvent() : TimedEvent<MockTimedEvent>(TimeSpan.FromSeconds(2))
{
    /// <inheritdoc />
    override protected TimedEventCall<MockTimedEvent> CreateEventCall() => new MockTimedEventCall();
}

public record MockTimedEventCall(int BestNumber = 42) : TimedEventCall<MockTimedEvent>;


public class TimedEventTests
{
    [Test]
    public async Task TimedEvent_ShouldTriggerAfterInterval()
    {
        // Arrange
        var timedEvent = new MockTimedEvent();
        bool eventTriggered = false;
        int bestNumber = 0;
        TimeSpan? interval = null;

        await timedEvent.Subscribe<MockTimedEventCall>(async eventCall =>
        {
            bestNumber = eventCall.BestNumber;
            eventTriggered = true;
            interval = eventCall.Interval;
            await Task.CompletedTask;
        });

        // Act
        timedEvent.Start();
        await Task.Delay(2500); // Wait for more than the interval

        // Assert
        await Assert.That(eventTriggered).IsTrue();
        await Assert.That(bestNumber).IsEqualTo(42);
        await Assert.That(interval).IsEqualTo(TimeSpan.FromSeconds(2));
    }
}