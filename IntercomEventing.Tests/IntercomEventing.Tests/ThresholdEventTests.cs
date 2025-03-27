using IntercomEventing.Features.Events;

namespace IntercomEventing.Tests;

public record MockThresholdEvent(int Threshold) : ThresholdEvent<MockThresholdEvent, int>(Threshold)
{
    /// <inheritdoc />
    override protected MockThresholdEventCall CreateEventCall() => new();
}

public record MockThresholdEventCall : ThresholdEventCall<MockThresholdEvent, int>;




public class ThresholdEventTests
{
    [Test]
    public async Task ThresholdEvent_ShouldTriggerWhenThresholdIsReached()
    {
        // Arrange
        var thresholdReachedEvent = new MockThresholdEvent(2);
        bool thresholdReached = false;
        int finalCount = 0;
        int thresholdValue = 0;
        
        Console.WriteLine($"Threshold reached event created with threshold {thresholdReachedEvent.Threshold}");

        await thresholdReachedEvent.Subscribe<MockThresholdEventCall>(async eventCall =>
        {
            thresholdReached = true;
            finalCount = eventCall.CurrentValue;
            thresholdValue = eventCall.Threshold;
            await Task.CompletedTask;
        });

        // Act
        await thresholdReachedEvent.IncrementValue(1); // Count = 1
        await thresholdReachedEvent.IncrementValue(2); // Count = 3

        // Assert
        await Assert.That(thresholdReached).IsTrue();
        await Assert.That(finalCount).IsEqualTo(3);
        await Assert.That(thresholdValue).IsEqualTo(2);
    }
}