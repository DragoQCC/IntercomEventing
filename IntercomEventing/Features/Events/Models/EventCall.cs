namespace IntercomEventing.Features.Events;

public record EventCall<TEvent> where TEvent : GenericEvent<TEvent>
{
    public EventMetadata Metadata { get; internal set; } = new();
}