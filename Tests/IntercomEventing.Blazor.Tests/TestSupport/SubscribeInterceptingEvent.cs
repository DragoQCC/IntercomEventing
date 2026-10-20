namespace IntercomEventing.Blazor.Tests.TestSupport;

/// <summary>
/// Wraps a real event and runs a callback at the start of every subscribe call, so a test can dispose a component's
/// subscriptions at the exact moment a subscribe call is in progress, as another thread could.
/// </summary>
public sealed class SubscribeInterceptingEvent<TCall> : IIntercomEvent<TCall>
    where TCall : EventCall
{
    private readonly IIntercomEvent<TCall> _innerEvent;
    private readonly Action _beforeSubscribe;

    public Guid Id => _innerEvent.Id;

    public string Name => _innerEvent.Name;

    public int SubscriberCount => _innerEvent.SubscriberCount;

    public EventingOptions Options => _innerEvent.Options;

    public event Func<TCall, Task> Raised
    {
        add
        {
            _innerEvent.Raised += value;
        }
        remove
        {
            _innerEvent.Raised -= value;
        }
    }

    public SubscribeInterceptingEvent(IIntercomEvent<TCall> innerEvent, Action beforeSubscribe)
    {
        _innerEvent = innerEvent;
        _beforeSubscribe = beforeSubscribe;
    }

    public EventSubscription<TCall> Subscribe(Func<TCall, Task> handler, Action<Exception, TCall>? onException = null)
    {
        _beforeSubscribe();
        return _innerEvent.Subscribe(handler, onException);
    }

    public EventSubscription<TCall> Subscribe(Action<TCall> handler, Action<Exception, TCall>? onException = null)
    {
        _beforeSubscribe();
        return _innerEvent.Subscribe(handler, onException);
    }

    public ValueTask<EventSubscription<TCall>> SubscribeAsync(Func<TCall, Task> handler, SubscriptionOptions<TCall>? options = null,
        CancellationToken cancellationToken = default)
    {
        _beforeSubscribe();
        return _innerEvent.SubscribeAsync(handler, options, cancellationToken);
    }

    public ValueTask<EventSubscription<TCall>> SubscribeAsync(Action<TCall> handler, SubscriptionOptions<TCall>? options = null,
        CancellationToken cancellationToken = default)
    {
        _beforeSubscribe();
        return _innerEvent.SubscribeAsync(handler, options, cancellationToken);
    }

    public bool Unsubscribe(Func<TCall, Task> handler)
    {
        return _innerEvent.Unsubscribe(handler);
    }

    public bool Unsubscribe(Action<TCall> handler)
    {
        return _innerEvent.Unsubscribe(handler);
    }
}
