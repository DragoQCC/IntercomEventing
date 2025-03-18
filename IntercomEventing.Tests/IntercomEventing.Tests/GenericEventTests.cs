using IntercomEventing.Benchmark.DebouncedEventExample;
using IntercomEventing.Benchmark.ThresholdEventExample;
using IntercomEventing.Benchmark.UserCreationExample;

namespace IntercomEventing.Tests;

public class GenericEventTests
{
    [Test]
    public async Task UserCreatedEvent_ShouldTriggerWithCorrectUserData()
    {
        // Arrange
        var userCreationProducer = new UserCreation_EventProducer();
        User? createdUser = null;

        await userCreationProducer.UserCreatedEvent.Subscribe<UserCreatedEventCall>(async eventCall =>
        {
            createdUser = eventCall.User;
            await Task.CompletedTask;
        });

        // Act
        var expectedUser = new User("Test User", "test@example.com");
        await userCreationProducer.CreateUserAsync(expectedUser);

        // Assert
        await Assert.That(createdUser).IsNotNull();
        await Assert.That(createdUser!.Name).IsEqualTo(expectedUser.Name);
        await Assert.That(createdUser.Email).IsEqualTo(expectedUser.Email);
    }
    
}
