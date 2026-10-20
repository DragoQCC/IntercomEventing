namespace IntercomEventing.Blazor.Tests.TestSupport;

//payloads and producers used by the sample components; in an application these would be its own services

public sealed record UserJoined(string Name);

public sealed record OrderPlaced(int OrderNumber, decimal Total);

/// <summary>
/// Options for test events: the raise waits for every handler, so a test can assert as soon as <c>RaiseAsync</c> returns
/// instead of depending on the one-second default timeout on a slow machine.
/// </summary>
public static class TestEventOptions
{
    public static EventingOptions WaitForEveryHandler { get; } = new()
    {
        StartNextEventHandlerAfter = null,
    };
}

/// <summary>
/// A producer the components receive by injection.
/// </summary>
public sealed class UserDirectory
{
    public IntercomEvent<UserJoined> UserJoined { get; } = new(TestEventOptions.WaitForEveryHandler);

    public Task AddAsync(string name)
    {
        return UserJoined.RaiseAsync(new UserJoined(name), caller: this);
    }
}

/// <summary>
/// A producer whose event is also handed to components as a parameter or cascading value.
/// </summary>
public sealed class OrderBook
{
    private int _lastOrderNumber;

    public IntercomEvent<OrderPlaced> OrderPlaced { get; } = new(TestEventOptions.WaitForEveryHandler);

    public Task PlaceAsync(decimal total)
    {
        var orderNumber = Interlocked.Increment(ref _lastOrderNumber);
        return OrderPlaced.RaiseAsync(new OrderPlaced(orderNumber, total), caller: this);
    }
}
