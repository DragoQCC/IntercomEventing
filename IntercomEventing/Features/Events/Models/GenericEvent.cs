

namespace IntercomEventing.Features.Events;

public abstract record GenericEvent<TEvent> : IEvent<TEvent> where TEvent : IEvent<TEvent>
{
    /// <inheritdoc />
    public EventMetadata Metadata { get; protected set; } = new();

    //implement the interface type diretly so I can expose a version with the correct accessibility
    EventMetadata IEvent<TEvent>.Metadata
    {
        get => Metadata;
        set => Metadata = value;
    }



    /// <inheritdoc />
    public HashSet<Subscription<TEvent>> Subscribers { get; init; } = new();
}

public abstract record GenericEvent<TEvent,TEventArgs> : IEvent<TEvent,TEventArgs>
where TEvent : IEvent<TEvent,TEventArgs>
where TEventArgs : IEventArgs<TEvent>
{
    /// <inheritdoc />
    public EventMetadata Metadata { get; protected set; } = new();

    //implement the interface type diretly so I can expose a version with the correct accessibility
    EventMetadata IEvent<TEvent>.Metadata
    {
        get => Metadata;
        set => Metadata = value;
    }

    /// <inheritdoc />
    public HashSet<Subscription<TEvent>> Subscribers { get; init; } = new();

    /// <inheritdoc />
    public TEventArgs EventArgs { get; set; }

}