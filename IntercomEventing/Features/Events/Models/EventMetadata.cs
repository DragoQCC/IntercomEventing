namespace IntercomEventing.Features.Events;

/*public record struct EventMetadata
{
    public Guid Id { get; init; } = Guid.CreateVersion7(DateTime.UtcNow);
    public DateTime LastEventTime { get; set; } = DateTime.UtcNow;
    public DateTime CreationTime { get; init; } = DateTime.UtcNow;
    //If the type of the event provider is known at compile time because we are subscribing to an event that class exposes should this be a generic type?
    public object? EventCaller { get; set; }
    public EventPriority Priority { get; init; } = EventPriority.Medium;

    internal ParallelOptions ParallelOptions = new()
    {
        MaxDegreeOfParallelism =  EventingConfiguration.EventingOptionsInternal.MaxThreadsPerEvent
    };

    public EventMetadata()
    {
    }
}*/

public class EventMetadata
{
    public Guid EventId { get; internal set; }
    public Guid EventCallId { get; init; } = Guid.CreateVersion7(DateTime.UtcNow);
    public DateTime LastEventTime { get; set; } = DateTime.UtcNow;
    public object? EventCaller { get; set; }
    public EventPriority Priority { get; init; } = EventPriority.Medium;
}