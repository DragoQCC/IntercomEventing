using IntercomEventing;

/// <summary>
/// A producer in the global namespace.
/// </summary>
public sealed partial class GlobalNamespaceProducer
{
    [IntercomEvent]
    private readonly IntercomEvent<int> _counted = new();

    public Task CountAsync(int count)
    {
        return _counted.RaiseAsync(count);
    }
}
