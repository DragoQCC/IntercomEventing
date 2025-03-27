namespace IntercomEventing.Features.Events;

/// <summary>
/// Represents a call to an event <br/>
/// Event calls are created when an event is raised and are passed to event handlers to provide context and data
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public record EventCall<TEvent> where TEvent : GenericEvent<TEvent>
{
    public EventMetadata Metadata { get; internal set; } = new();
}