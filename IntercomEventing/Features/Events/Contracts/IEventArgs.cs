namespace IntercomEventing.Features.Events;

public interface IEventArgs;

public interface IEventArgs<TEvent> : IEventArgs where TEvent : IEvent;