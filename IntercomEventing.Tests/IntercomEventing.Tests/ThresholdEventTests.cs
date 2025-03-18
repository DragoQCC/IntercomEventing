using IntercomEventing.Benchmark.ThresholdEventExample;

namespace IntercomEventing.Tests;

public class ThresholdEventTests
{
    [Test]
    public async Task ThresholdEvent_ShouldTriggerWhenThresholdIsReached()
    {
        // Arrange
        var thresholdEventProducer = new ThresholdReached_IntercomEventProducer();
        bool thresholdReached = false;
        int finalCount = 0;

        await thresholdEventProducer.ThresholdReachedEvent.Subscribe<CounterThresholdReachedEventCall>(async eventCall =>
        {
            thresholdReached = true;
            finalCount = eventCall.CurrentValue;
            await Task.CompletedTask;
        });

        // Act
        await thresholdEventProducer.IncrementCountEvent(); // Count = 1
        await thresholdEventProducer.IncrementCountEvent(); // Count = 2

        // Assert
        await Assert.That(thresholdReached).IsTrue();
        await Assert.That(finalCount).IsEqualTo(2);
    }
}