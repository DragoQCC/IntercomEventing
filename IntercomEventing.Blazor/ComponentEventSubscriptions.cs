using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor;

/// <summary>
/// Owns the event subscriptions of one Blazor component: every handler runs on the component's renderer, the component
/// re-renders afterwards, and every subscription is removed when the component is disposed. Works the same on Blazor Server
/// and WebAssembly, and never blocks a thread.
/// </summary>
/// <remarks>
/// <para>Derive from <see cref="IntercomComponentBase"/> to get one as <c>Events</c>. Create one yourself when the component
/// already has another base class: <c>InvokeAsync</c>, <c>StateHasChanged</c>, <c>DispatchExceptionAsync</c> and
/// <c>RendererInfo</c> are protected members of <see cref="ComponentBase"/>, so the component passes them in and disposes the
/// helper from its own <c>DisposeAsync</c>.</para>
/// <list type="bullet">
/// <item><description>Rendering: events may be raised from any thread. Like a Blazor UI event handler, the component
/// re-renders when an asynchronous handler first awaits and again when it finishes.</description></item>
/// <item><description>Exceptions: a handler's exception goes to the subscription's own exception handler when one is
/// given (it runs on the renderer too, and the component re-renders afterwards); else to the component's
/// <c>DispatchExceptionAsync</c>, so the nearest <c>ErrorBoundary</c> shows it; else (no <c>dispatchExceptionAsync</c>
/// supplied) to the event's normal routing. A dispatched exception that no <c>ErrorBoundary</c> catches is unhandled, like
/// any exception from a component's event handler: it ends a Blazor Server circuit and shows the error UI on
/// WebAssembly.</description></item>
/// <item><description>Removed subscriptions: a call that is still waiting for the renderer when its subscription is removed
/// (its handle is disposed, <c>Resubscribe</c> moves it, or the event is disposed) is skipped, as the event itself skips a
/// handler it has not started yet.</description></item>
/// <item><description>Cancellation: a handler whose task ends cancelled, for example by an <c>HttpClient</c> timeout or a token
/// the component cancels (also while it is disposed), counts as cancelled, as with a Blazor UI event handler: it is reported
/// nowhere and the component does not re-render for it. A handler that throws <see cref="OperationCanceledException"/>
/// before returning its task fails like any other synchronous throw, which is what Blazor does too.</description></item>
/// <item><description>Exceptions after disposal: an exception bound for the component (its own exception handler or its
/// <c>ErrorBoundary</c>) that can no longer reach it, because the component was disposed while the handler ran or its
/// renderer is gone, is reported as <see cref="EventFailureStage.Handler"/> to the event's own
/// <see cref="EventingOptions.DefaultExceptionHandler"/>: the one in <see cref="IIntercomEvent{TCall}.Options"/>, which is
/// <see cref="EventingOptions.Default"/> for an event created without options. The options are read when the failure is
/// reported, so a change to <see cref="EventingOptions.Default"/> made while the handler ran applies to it. A subscription's
/// own exception handler never runs for a disposed component.</description></item>
/// <item><description>Prerendering: when <c>getRendererInfo</c> is supplied, subscribing while the component renders
/// statically (static server rendering or prerendering, <see cref="RendererInfo.IsInteractive"/> is <see langword="false"/>)
/// subscribes nothing and returns <see langword="null"/>. That output can never update; the interactive instance of the
/// component subscribes when it initializes.</description></item>
/// <item><description>Disposal never throws. Subscribing after it subscribes nothing and returns <see langword="null"/>,
/// so a lifecycle method that finishes after the user navigated away does not fail.</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// public partial class OrderTicker : IAsyncDisposable
/// {
///     private readonly ComponentEventSubscriptions _events;
///     private decimal _lastTotal;
///
///     [Inject]
///     private OrderBook Orders { get; set; } = default!;
///
///     public OrderTicker()
///     {
///         _events = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
///     }
///
///     protected override void OnInitialized()
///     {
///         _events.Subscribe(Orders.OrderPlaced, call => _lastTotal = call.Payload.Total);
///     }
///
///     public ValueTask DisposeAsync()
///     {
///         return _events.DisposeAsync();
///     }
/// }
/// </code>
/// </example>
public sealed class ComponentEventSubscriptions : IAsyncDisposable, IDisposable
{
    private readonly Lock _subscriptionsLock = new();

    //subscriptions made through this helper that may still be active; they are removed when the component is disposed
    private readonly List<TrackedSubscription> _subscriptions = [];

    private readonly Func<Func<Task>, Task> _invokeAsync;
    private readonly Action _stateHasChanged;
    private readonly Func<Exception, Task>? _dispatchExceptionAsync;
    private readonly Func<RendererInfo>? _getRendererInfo;
    private int _disposedFlag;

    /// <summary>
    /// <see langword="true"/> once the component disposed this helper. Handlers no longer run and new subscriptions are not made.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposedFlag) == 1;

    /// <summary>
    /// Creates the subscription owner for one component. Pass the component's own protected members.
    /// </summary>
    /// <param name="invokeAsync">The component's <c>InvokeAsync</c>; the method group binds to the <c>Func&lt;Task&gt;</c> overload.</param>
    /// <param name="stateHasChanged">The component's <c>StateHasChanged</c>.</param>
    /// <param name="dispatchExceptionAsync">Optional: the component's <c>DispatchExceptionAsync</c>, so handler exceptions reach
    /// the nearest <c>ErrorBoundary</c>. When omitted, they follow the event's normal routing.</param>
    /// <param name="getRendererInfo">Optional: <c>() =&gt; RendererInfo</c>, so subscribing during static rendering or
    /// prerendering is skipped. When omitted, every subscribe call subscribes. A renderer that cannot report its info (bUnit
    /// before <c>SetRendererInfo</c>, or a component that is not attached yet) counts as interactive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="invokeAsync"/> or <paramref name="stateHasChanged"/> is
    /// <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// _events = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
    /// </code>
    /// </example>
    public ComponentEventSubscriptions(Func<Func<Task>, Task> invokeAsync, Action stateHasChanged,
        Func<Exception, Task>? dispatchExceptionAsync = null, Func<RendererInfo>? getRendererInfo = null)
    {
        ArgumentNullException.ThrowIfNull(invokeAsync);
        ArgumentNullException.ThrowIfNull(stateHasChanged);
        _invokeAsync = invokeAsync;
        _stateHasChanged = stateHasChanged;
        _dispatchExceptionAsync = dispatchExceptionAsync;
        _getRendererInfo = getRendererInfo;
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> to <paramref name="source"/> for the component's lifetime. The handler runs on the
    /// renderer, then the component re-renders.
    /// </summary>
    /// <remarks>
    /// Pass a single method or lambda. A combined delegate (<c>a + b</c>) is invoked as one delegate: only its last part's task
    /// is awaited, and a part that throws synchronously stops the parts after it. A handler task that fails with several
    /// exceptions (for example from <c>Task.WhenAll</c>) has only its first exception routed, as when Blazor awaits a UI event
    /// handler; handle the others inside the handler when they matter.
    /// </remarks>
    /// <typeparam name="TCall">The event's call type, inferred from <paramref name="source"/>.</typeparam>
    /// <param name="source">The event: from an injected service, a cascading value or a parameter.</param>
    /// <param name="handler">Runs on the renderer for each call and may update component state directly.</param>
    /// <param name="onException">Receives this handler's exceptions on the renderer, instead of the component's <c>ErrorBoundary</c>.</param>
    /// <param name="renderAfterHandler"><see langword="false"/> to skip the automatic <c>StateHasChanged</c>.</param>
    /// <returns>The subscription, which is removed with the component (dispose it to remove it earlier); <see langword="null"/>
    /// when nothing was subscribed because the component renders statically or was already disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The event allows only one subscriber and already has one.</exception>
    /// <example>
    /// <code>
    /// protected override void OnInitialized()
    /// {
    ///     Events.Subscribe(Orders.OrderPlaced, async call =>
    ///     {
    ///         _lastOrder = await Orders.LoadDetailsAsync(call.Payload.OrderNumber);
    ///     });
    /// }
    /// </code>
    /// </example>
    public EventSubscription<TCall>? Subscribe<TCall>(IIntercomEvent<TCall> source, Func<TCall, Task> handler,
        Action<Exception, TCall>? onException = null, bool renderAfterHandler = true)
        where TCall : EventCall
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        if (!CanSubscribeNow())
        {
            return null;
        }

        //the subscription exists only after the event created it, but its handler and exception route need it to tell whether a
        //late call still belongs to an active subscription, so they share this slot and the subscription is filled in afterwards
        var subscriptionSlot = new SubscriptionSlot();
        var exceptionRoute = CreateExceptionRoute(source, onException, renderAfterHandler, subscriptionSlot);
        Action<Exception, TCall>? exceptionHandler = exceptionRoute is null ? null : exceptionRoute.Handle;
        var subscription = source.Subscribe(CreateHandlerOnRenderer(handler, renderAfterHandler, subscriptionSlot), exceptionHandler);
        subscriptionSlot.Fill(subscription);
        if (TryTrack(source, subscription))
        {
            return subscription;
        }
        //the component was disposed on another thread while this call subscribed; do not leave the subscription behind
        subscription.Dispose();
        return null;
    }

    /// <summary>
    /// Subscribes a synchronous <paramref name="handler"/> to <paramref name="source"/> for the component's lifetime. The handler
    /// runs on the renderer, then the component re-renders.
    /// </summary>
    /// <remarks>
    /// <para>An <c>async void</c> method group also converts to <see cref="Action{T}"/>. Its exceptions then bypass every route
    /// above (the <c>ErrorBoundary</c>, <paramref name="onException"/> and the event's routing): they reach the renderer's
    /// dispatcher as unhandled exceptions and end a Blazor Server circuit. Write asynchronous handlers as methods or lambdas
    /// that return <see cref="Task"/>.</para>
    /// <para>Pass a single method or lambda. A combined delegate (<c>a + b</c>) is invoked as one delegate, so a part that throws
    /// stops the parts after it; the event's own <c>Subscribe</c> runs every part instead.</para>
    /// </remarks>
    /// <typeparam name="TCall">The event's call type, inferred from <paramref name="source"/>.</typeparam>
    /// <param name="source">The event: from an injected service, a cascading value or a parameter.</param>
    /// <param name="handler">Runs on the renderer for each call and may update component state directly.</param>
    /// <param name="onException">Receives this handler's exceptions on the renderer, instead of the component's <c>ErrorBoundary</c>.</param>
    /// <param name="renderAfterHandler"><see langword="false"/> to skip the automatic <c>StateHasChanged</c>.</param>
    /// <returns>The subscription, which is removed with the component; <see langword="null"/> when nothing was subscribed
    /// because the component renders statically or was already disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The event allows only one subscriber and already has one.</exception>
    /// <example>
    /// <code>
    /// protected override void OnInitialized()
    /// {
    ///     Events.Subscribe(Users.UserJoined, call => _recentNames.Insert(0, call.Payload.Name));
    /// }
    /// </code>
    /// </example>
    public EventSubscription<TCall>? Subscribe<TCall>(IIntercomEvent<TCall> source, Action<TCall> handler,
        Action<Exception, TCall>? onException = null, bool renderAfterHandler = true)
        where TCall : EventCall
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Subscribe(source, ToTaskReturningHandler(handler), onException, renderAfterHandler);
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> with lifecycle hooks; await it in <c>OnInitializedAsync</c>. The handler runs on the
    /// renderer; the hooks run where the event runs them (see remarks).
    /// </summary>
    /// <remarks>
    /// <para><see cref="SubscriptionOptions{TCall}.OnSubscribed"/> runs on the caller's context, so on the renderer when called
    /// from a lifecycle method. <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> runs on the renderer when the component's
    /// disposal or <c>Resubscribe</c> removes the subscription, but on whatever thread disposes the event when the producer
    /// disposes it first, such as a singleton at shutdown. A hook that touches component state should go through the
    /// component's <c>InvokeAsync</c>.</para>
    /// <para>If the component is disposed while <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> runs, the new subscription is
    /// removed again (running <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/>) and the result is <see langword="null"/>.</para>
    /// <para>Pass a single method or lambda: a combined delegate (<c>a + b</c>) is invoked as one delegate, as described on
    /// <see cref="Subscribe{TCall}(IIntercomEvent{TCall}, Func{TCall, Task}, Action{Exception, TCall}?, bool)"/>.</para>
    /// </remarks>
    /// <typeparam name="TCall">The event's call type, inferred from <paramref name="source"/>.</typeparam>
    /// <param name="source">The event.</param>
    /// <param name="handler">Runs on the renderer for each call.</param>
    /// <param name="options">Hooks, and an exception handler (run on the renderer) that replaces the <c>ErrorBoundary</c> routing.</param>
    /// <param name="renderAfterHandler"><see langword="false"/> to skip the automatic <c>StateHasChanged</c>.</param>
    /// <param name="cancellationToken">Stops waiting for <see cref="SubscriptionOptions{TCall}.OnSubscribed"/>.</param>
    /// <returns>The subscription once <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> finished; <see langword="null"/>
    /// when nothing was subscribed because the component renders statically or was disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The event allows only one subscriber and already has one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before or while
    /// <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> ran.</exception>
    /// <example>
    /// <code>
    /// protected override async Task OnInitializedAsync()
    /// {
    ///     await Events.SubscribeAsync(Chat.MessagePosted, OnMessagePostedAsync, new()
    ///     {
    ///         OnSubscribed = subscription => Chat.JoinRoomAsync(RoomId),
    ///         OnUnsubscribed = subscription => Chat.LeaveRoomAsync(RoomId),
    ///     });
    /// }
    /// </code>
    /// </example>
    public ValueTask<EventSubscription<TCall>?> SubscribeAsync<TCall>(IIntercomEvent<TCall> source, Func<TCall, Task> handler,
        SubscriptionOptions<TCall>? options = null, bool renderAfterHandler = true, CancellationToken cancellationToken = default)
        where TCall : EventCall
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeWithHooksAsync(source, handler, options, renderAfterHandler, cancellationToken);
    }

    /// <summary>
    /// Subscribes a synchronous <paramref name="handler"/> with lifecycle hooks; await it in <c>OnInitializedAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>Where the hooks run is described on the asynchronous-handler overload.</para>
    /// <para>An <c>async void</c> method group also converts to <see cref="Action{T}"/>, and its exceptions then bypass the
    /// <c>ErrorBoundary</c> and every exception handler and end a Blazor Server circuit; pass asynchronous handlers as methods or
    /// lambdas that return <see cref="Task"/>. A combined delegate (<c>a + b</c>) is invoked as one delegate, so a part that
    /// throws stops the parts after it.</para>
    /// </remarks>
    /// <typeparam name="TCall">The event's call type, inferred from <paramref name="source"/>.</typeparam>
    /// <param name="source">The event.</param>
    /// <param name="handler">Runs on the renderer for each call.</param>
    /// <param name="options">Hooks, and an exception handler (run on the renderer) that replaces the <c>ErrorBoundary</c> routing.</param>
    /// <param name="renderAfterHandler"><see langword="false"/> to skip the automatic <c>StateHasChanged</c>.</param>
    /// <param name="cancellationToken">Stops waiting for <see cref="SubscriptionOptions{TCall}.OnSubscribed"/>.</param>
    /// <returns>The subscription once <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> finished; <see langword="null"/>
    /// when nothing was subscribed because the component renders statically or was disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The event allows only one subscriber and already has one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before or while
    /// <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> ran.</exception>
    /// <example>
    /// <code>
    /// await Events.SubscribeAsync(Presence.UserOnline, call => _online.Add(call.Payload.Name), new()
    /// {
    ///     OnSubscribed = subscription => Presence.RefreshAsync(),
    /// });
    /// </code>
    /// </example>
    public ValueTask<EventSubscription<TCall>?> SubscribeAsync<TCall>(IIntercomEvent<TCall> source, Action<TCall> handler,
        SubscriptionOptions<TCall>? options = null, bool renderAfterHandler = true, CancellationToken cancellationToken = default)
        where TCall : EventCall
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeAsync(source, ToTaskReturningHandler(handler), options, renderAfterHandler, cancellationToken);
    }

    /// <summary>
    /// Keeps one subscription on an event that can change between renders, such as a <c>[Parameter]</c> or a
    /// <c>[CascadingParameter]</c>. Call it from <c>OnParametersSet</c> and store the result.
    /// </summary>
    /// <remarks>
    /// <para>Nothing changes while <paramref name="source"/> is the event <paramref name="currentSubscription"/> belongs to and
    /// that subscription is still active. Otherwise <paramref name="source"/> is subscribed first, and then the current
    /// subscription is removed (an <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook runs in the background, as with
    /// <see cref="EventSubscription{TCall}.Dispose"/>). A current subscription that was already removed, by disposing its handle
    /// or by the event's disposal, is therefore replaced rather than kept.</para>
    /// <para>When subscribing to the new <paramref name="source"/> throws, the current subscription is left as it was and still
    /// receives its event's calls, so the field that holds it stays valid and a later call that returns to its event keeps
    /// it.</para>
    /// <para>Pass a single method or lambda: a combined delegate (<c>a + b</c>) is invoked as one delegate, as described on
    /// <see cref="Subscribe{TCall}(IIntercomEvent{TCall}, Func{TCall, Task}, Action{Exception, TCall}?, bool)"/>.</para>
    /// <para>The handler and options of a kept subscription stay as they were; a handler that reads the component's current
    /// parameters sees their new values anyway.</para>
    /// <para>To hand one event to many components without passing it down, register its owner as a service and cascade the
    /// event with the framework's <c>AddCascadingValue</c>; a <c>[CascadingParameter]</c> of the subscribe-only
    /// <see cref="IIntercomEvent{TCall}"/> type receives it:</para>
    /// <code>
    /// builder.Services.AddScoped&lt;OrderBook&gt;();
    /// builder.Services.AddCascadingValue(services =&gt; services.GetRequiredService&lt;OrderBook&gt;().OrderPlaced);
    /// </code>
    /// <para>Choose the lifetime deliberately: on Blazor Server a singleton event is shared by every user's circuit, while a
    /// scoped one lives for one circuit. On WebAssembly both mean one per browser tab.</para>
    /// </remarks>
    /// <typeparam name="TCall">The event's call type.</typeparam>
    /// <param name="currentSubscription">The result of the previous call, or <see langword="null"/>.</param>
    /// <param name="source">The event to be subscribed to now, or <see langword="null"/> for none.</param>
    /// <param name="handler">Runs on the renderer for each call of a new subscription.</param>
    /// <param name="onException">Receives the handler's exceptions on the renderer, instead of the component's <c>ErrorBoundary</c>.</param>
    /// <param name="renderAfterHandler"><see langword="false"/> to skip the automatic <c>StateHasChanged</c>.</param>
    /// <returns>The subscription to <paramref name="source"/>; <see langword="null"/> when <paramref name="source"/> is
    /// <see langword="null"/> or nothing was subscribed (static rendering, disposed component).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">A new <paramref name="source"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A new <paramref name="source"/> allows only one subscriber and already has one.</exception>
    /// <example>
    /// <code>
    /// [CascadingParameter]
    /// public IIntercomEvent&lt;EventCall&lt;OrderPlaced&gt;&gt;? OrderPlaced { get; set; }
    ///
    /// protected override void OnParametersSet()
    /// {
    ///     _orderSubscription = Events.Resubscribe(_orderSubscription, OrderPlaced, call => _orderCount++);
    /// }
    /// </code>
    /// </example>
    public EventSubscription<TCall>? Resubscribe<TCall>(EventSubscription<TCall>? currentSubscription, IIntercomEvent<TCall>? source,
        Func<TCall, Task> handler, Action<Exception, TCall>? onException = null, bool renderAfterHandler = true)
        where TCall : EventCall
    {
        ArgumentNullException.ThrowIfNull(handler);
        //EventSubscription.Event is the event instance itself, so reference equality tells whether the event changed; a
        //subscription removed meanwhile (its handle disposed, its event disposed) no longer receives calls and is replaced
        var currentIsActive = currentSubscription is { IsActive: true };
        var alreadyOnThisEvent = currentIsActive && ReferenceEquals(currentSubscription!.Event, source);
        if (alreadyOnThisEvent)
        {
            return currentSubscription;
        }
        //subscribed before the current subscription is removed: when this throws (a disposed event, a single-subscriber event
        //that is taken), the caller keeps a working subscription instead of a dead one it can never replace
        EventSubscription<TCall>? newSubscription = null;
        if (source is not null)
        {
            newSubscription = Subscribe(source, handler, onException, renderAfterHandler);
        }
        if (currentSubscription is not null)
        {
            Untrack(currentSubscription);
            //removes the handler at once; the core runs an OnUnsubscribed hook in the background and routes its failure
            currentSubscription.Dispose();
        }
        return newSubscription;
    }

    /// <summary>
    /// Keeps one synchronous-handler subscription on an event that can change between renders. Call it from
    /// <c>OnParametersSet</c> and store the result; see the asynchronous-handler overload for the details.
    /// </summary>
    /// <remarks>
    /// An <c>async void</c> method group also converts to <see cref="Action{T}"/>, and its exceptions then bypass the
    /// <c>ErrorBoundary</c> and every exception handler and end a Blazor Server circuit; pass asynchronous handlers as methods or
    /// lambdas that return <see cref="Task"/>. A combined delegate (<c>a + b</c>) is invoked as one delegate, so a part that
    /// throws stops the parts after it.
    /// </remarks>
    /// <typeparam name="TCall">The event's call type.</typeparam>
    /// <param name="currentSubscription">The result of the previous call, or <see langword="null"/>.</param>
    /// <param name="source">The event to be subscribed to now, or <see langword="null"/> for none.</param>
    /// <param name="handler">Runs on the renderer for each call of a new subscription.</param>
    /// <param name="onException">Receives the handler's exceptions on the renderer, instead of the component's <c>ErrorBoundary</c>.</param>
    /// <param name="renderAfterHandler"><see langword="false"/> to skip the automatic <c>StateHasChanged</c>.</param>
    /// <returns>The subscription to <paramref name="source"/>; <see langword="null"/> when <paramref name="source"/> is
    /// <see langword="null"/> or nothing was subscribed (static rendering, disposed component).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">A new <paramref name="source"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A new <paramref name="source"/> allows only one subscriber and already has one.</exception>
    /// <example>
    /// <code>
    /// protected override void OnParametersSet()
    /// {
    ///     _selectionSubscription = Events.Resubscribe(_selectionSubscription, SelectionChanged, call => _selected = call.Payload);
    /// }
    /// </code>
    /// </example>
    public EventSubscription<TCall>? Resubscribe<TCall>(EventSubscription<TCall>? currentSubscription, IIntercomEvent<TCall>? source,
        Action<TCall> handler, Action<Exception, TCall>? onException = null, bool renderAfterHandler = true)
        where TCall : EventCall
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Resubscribe(currentSubscription, source, ToTaskReturningHandler(handler), onException, renderAfterHandler);
    }

    /// <summary>
    /// Removes every subscription and awaits their unsubscribe hooks. Call it from the component's <c>DisposeAsync</c>.
    /// </summary>
    /// <remarks>
    /// Never throws. A failing <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook is reported as
    /// <see cref="EventFailureStage.OnUnsubscribed"/> to the <see cref="EventingOptions.DefaultExceptionHandler"/> of its event's
    /// <see cref="IIntercomEvent{TCall}.Options"/> (<see cref="EventingOptions.Default"/> for an event created without options),
    /// and the remaining subscriptions are still removed. Calling it again does nothing.
    /// </remarks>
    /// <returns>A task that completes when every subscription is removed and its hook has finished.</returns>
    /// <example>
    /// <code>
    /// public ValueTask DisposeAsync()
    /// {
    ///     return _events.DisposeAsync();
    /// }
    /// </code>
    /// </example>
    public async ValueTask DisposeAsync()
    {
        foreach (var trackedSubscription in MarkDisposedAndTakeSubscriptions())
        {
            await RemoveAndReportFailureAsync(trackedSubscription);
        }
    }

    /// <summary>
    /// Removes every subscription immediately, for components that implement only <see cref="IDisposable"/>. Unsubscribe hooks
    /// run in the background and the event routes their failures. Never blocks and never throws.
    /// </summary>
    /// <example>
    /// <code>
    /// public void Dispose()
    /// {
    ///     _events.Dispose();
    /// }
    /// </code>
    /// </example>
    public void Dispose()
    {
        foreach (var trackedSubscription in MarkDisposedAndTakeSubscriptions())
        {
            trackedSubscription.Subscription.Dispose();
        }
    }

    private static Func<TCall, Task> ToTaskReturningHandler<TCall>(Action<TCall> handler)
    {
        return call =>
        {
            handler(call);
            return Task.CompletedTask;
        };
    }

    //removes one subscription and awaits its hook; a failing hook is reported instead of thrown, so disposal always finishes
    private static async ValueTask RemoveAndReportFailureAsync(TrackedSubscription trackedSubscription)
    {
        try
        {
            //no ConfigureAwait(false): the hooks of later subscriptions then run on the component's context, like the rest of its disposal
            await trackedSubscription.Subscription.DisposeAsync();
        }
        catch (Exception exception)
        {
            var failure = new EventHandlerFailure(exception, EventFailureStage.OnUnsubscribed, trackedSubscription.EventName,
                trackedSubscription.Subscription.Id, Call: null);
            ReportToEventExceptionHandler(failure, trackedSubscription.ReadEventOptions);
        }
    }

    //a failure this helper took over from the core but cannot deliver goes where the core would have sent it: the event's own
    //DefaultExceptionHandler, which is EventingOptions.Default's for an event created without options
    private static void ReportToEventExceptionHandler(EventHandlerFailure failure, Func<EventingOptions> readEventOptions)
    {
        try
        {
            //read when the failure is reported, not when the raise started: the exception route receives only the exception and
            //the call, not the options of the raise it came from. For a failing hook the core reads them at this point too.
            //Inside the try, because a custom IIntercomEvent implementation could throw from its Options getter
            var eventOptions = readEventOptions();
            eventOptions.DefaultExceptionHandler?.Invoke(failure);
        }
        catch (Exception reportingFailure)
        {
            //like the core: a throwing exception handler must not escape from disposal or from a background dispatch
            var failureType = reportingFailure.GetType().Name;
            Debug.WriteLine($"IntercomEventing.Blazor: reporting a {failure.Stage} failure of {failure.EventName} threw {failureType}: {reportingFailure.Message}");
        }
    }

    private bool CanSubscribeNow()
    {
        return !IsDisposed && !IsRenderingStatically();
    }

    //static server rendering and prerendering send their output once and never update it, so a subscription there would only leak work
    private bool IsRenderingStatically()
    {
        if (_getRendererInfo is null)
        {
            return false;
        }
        try
        {
            return !_getRendererInfo().IsInteractive;
        }
        catch (Exception)
        {
            //the renderer cannot say: bUnit throws its own exception type until SetRendererInfo is called, and a component that
            //is not attached yet throws InvalidOperationException. Subscribing is what would have happened without the guard
            return false;
        }
    }

    //the subscription's own exception handler wins, else the component's ErrorBoundary; null leaves the routing to the event
    private ExceptionRoute<TCall>? CreateExceptionRoute<TCall>(IIntercomEvent<TCall> source, Action<Exception, TCall>? subscriptionExceptionHandler,
        bool renderAfterHandler, SubscriptionSlot subscriptionSlot)
        where TCall : EventCall
    {
        var noComponentRouting = subscriptionExceptionHandler is null && _dispatchExceptionAsync is null;
        if (noComponentRouting)
        {
            return null;
        }
        return new ExceptionRoute<TCall>(this, source, subscriptionExceptionHandler, renderAfterHandler, subscriptionSlot);
    }

    private async ValueTask<EventSubscription<TCall>?> SubscribeWithHooksAsync<TCall>(IIntercomEvent<TCall> source, Func<TCall, Task> handler,
        SubscriptionOptions<TCall>? options, bool renderAfterHandler, CancellationToken cancellationToken)
        where TCall : EventCall
    {
        if (!CanSubscribeNow())
        {
            return null;
        }

        var subscriptionSlot = new SubscriptionSlot();
        var exceptionRoute = CreateExceptionRoute(source, options?.OnException, renderAfterHandler, subscriptionSlot);
        var componentOptions = new SubscriptionOptions<TCall>
        {
            OnSubscribed = options?.OnSubscribed,
            OnUnsubscribed = options?.OnUnsubscribed,
            OnException = exceptionRoute is null ? null : exceptionRoute.Handle,
        };
        var handlerOnRenderer = CreateHandlerOnRenderer(handler, renderAfterHandler, subscriptionSlot);
        //no ConfigureAwait(false): when the component was disposed meanwhile, OnUnsubscribed below runs on the caller's context
        var subscription = await source.SubscribeAsync(handlerOnRenderer, componentOptions, cancellationToken);
        subscriptionSlot.Fill(subscription);
        if (TryTrack(source, subscription))
        {
            return subscription;
        }
        //the component was disposed while OnSubscribed ran: remove the subscription again, which runs OnUnsubscribed to undo the hook
        await RemoveAndReportFailureAsync(TrackedSubscription.For(source, subscription));
        return null;
    }

    private Func<TCall, Task> CreateHandlerOnRenderer<TCall>(Func<TCall, Task> handler, bool renderAfterHandler, SubscriptionSlot subscriptionSlot)
    {
        return call => RunOnRendererAsync(handler, call, renderAfterHandler, subscriptionSlot);
    }

    private async Task RunOnRendererAsync<TCall>(Func<TCall, Task> handler, TCall call, bool renderAfterHandler, SubscriptionSlot subscriptionSlot)
    {
        if (IsDisposed || subscriptionSlot.WasRemoved)
        {
            return;
        }
        var handlerStarted = false;
        try
        {
            //Server: queued on the circuit's synchronization context; WebAssembly: runs on the single UI thread
            await _invokeAsync(async () =>
            {
                //while this call waited for the renderer, the component may have been disposed, or the subscription removed
                //(its handle disposed, Resubscribe moving it, the event disposed); like the event, which skips a handler it has
                //not started once its subscription is gone, the call is then dropped
                var callNoLongerWanted = IsDisposed || subscriptionSlot.WasRemoved;
                if (callNoLongerWanted)
                {
                    return;
                }
                handlerStarted = true;
                await RunHandlerThenRenderAsync(handler, call, renderAfterHandler);
            });
        }
        catch (Exception) when (!handlerStarted && IsDisposed)
        {
            //the renderer refused the work because it went away together with the component (a closed Server circuit)
        }
    }

    //runs on the renderer; renders the way Blazor renders after a UI event handler
    private async Task RunHandlerThenRenderAsync<TCall>(Func<TCall, Task> handler, TCall call, bool renderAfterHandler)
    {
        //a handler that returns null is treated as finished, as the core does
        var handlerTask = handler(call) ?? Task.CompletedTask;
        var handlerIsStillRunning = !handlerTask.IsCompleted;
        if (renderAfterHandler && handlerIsStillRunning)
        {
            //shows what the handler set before its first await, such as a loading indicator
            RenderIfNotDisposed();
        }
        try
        {
            //no ConfigureAwait(false): the render below must run on the renderer's context. A failure skips the render and is routed
            await handlerTask;
        }
        catch (OperationCanceledException) when (handlerTask.IsCanceled)
        {
            //Blazor ignores a UI event handler whose task was cancelled (an HttpClient timeout, a Cancel button, a token the
            //component cancels while it is disposed) instead of handing it to the ErrorBoundary, so this does too: the call
            //counts as finished, nothing is reported and there is no render afterwards
            return;
        }
        if (renderAfterHandler)
        {
            RenderIfNotDisposed();
        }
    }

    private void RenderIfNotDisposed()
    {
        if (!IsDisposed)
        {
            _stateHasChanged();
        }
    }

    //false when the exception could not reach the component, so the caller reports it instead
    private async Task<bool> TryDispatchToComponentAsync(Exception exception)
    {
        if (IsDisposed || _dispatchExceptionAsync is null)
        {
            return false;
        }
        try
        {
            //DispatchExceptionAsync moves to the renderer itself and hands the exception to the nearest ErrorBoundary
            await _dispatchExceptionAsync(exception);
            return true;
        }
        catch (Exception)
        {
            //the component left the renderer between the check and the dispatch (ArgumentException: unknown component id)
            return false;
        }
    }

    //runs the subscription's own exception handler on the renderer, then re-renders, so it can update component state directly.
    //false when it could not run (the component was disposed, or the renderer refused the work): the caller then reports the exception
    private async Task<bool> TryRunExceptionHandlerOnRendererAsync<TCall>(Action<Exception, TCall> exceptionHandler, Exception exception,
        TCall call, bool renderAfterHandler)
    {
        //the handler failed after the component was disposed: its exception handler would only touch a dead component
        if (IsDisposed)
        {
            return false;
        }
        var exceptionHandlerStarted = false;
        try
        {
            await _invokeAsync(() =>
            {
                //the component may have been disposed while this work waited for the renderer
                if (IsDisposed)
                {
                    return Task.CompletedTask;
                }
                exceptionHandlerStarted = true;
                exceptionHandler(exception, call);
                if (renderAfterHandler)
                {
                    RenderIfNotDisposed();
                }
                return Task.CompletedTask;
            });
            return exceptionHandlerStarted;
        }
        catch (Exception exceptionHandlerFailure) when (exceptionHandlerStarted)
        {
            //like the core: an exception thrown by an exception handler is contained
            var failureType = exceptionHandlerFailure.GetType().Name;
            Debug.WriteLine($"IntercomEventing.Blazor: subscription exception handler threw {failureType}: {exceptionHandlerFailure.Message}");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    //adds a new subscription to the ones removed at disposal; false when the component was disposed meanwhile
    private bool TryTrack<TCall>(IIntercomEvent<TCall> source, EventSubscription<TCall> subscription)
        where TCall : EventCall
    {
        lock (_subscriptionsLock)
        {
            if (IsDisposed)
            {
                return false;
            }
            //subscriptions removed by another route (their own handle, the event's disposal) need no disposing; dropping them keeps the list short
            _subscriptions.RemoveAll(static trackedSubscription => !trackedSubscription.Subscription.IsActive);
            _subscriptions.Add(TrackedSubscription.For(source, subscription));
            return true;
        }
    }

    private void Untrack(IEventSubscription subscription)
    {
        lock (_subscriptionsLock)
        {
            _subscriptions.RemoveAll(trackedSubscription => ReferenceEquals(trackedSubscription.Subscription, subscription));
        }
    }

    private TrackedSubscription[] MarkDisposedAndTakeSubscriptions()
    {
        lock (_subscriptionsLock)
        {
            //set under the lock so TryTrack either sees it or has already added its subscription to the list taken here
            Volatile.Write(ref _disposedFlag, 1);
            TrackedSubscription[] subscriptionsToRemove = [.. _subscriptions];
            _subscriptions.Clear();
            return subscriptionsToRemove;
        }
    }

    /// <summary>
    /// A subscription kept for disposal, with what is needed to report its failing unsubscribe hook. The list holds
    /// subscriptions of different call types, and <see cref="IEventSubscription"/> does not expose its event, so the name of
    /// the event it was subscribed through and a way to read that event's options when the hook fails are kept with it.
    /// </summary>
    private readonly record struct TrackedSubscription(IEventSubscription Subscription, string EventName, Func<EventingOptions> ReadEventOptions)
    {
        //the source, not subscription.Event, so a failing hook is reported through the same event as a failing handler
        //(ExceptionRoute exists before the subscription does and only knows the source)
        public static TrackedSubscription For<TCall>(IIntercomEvent<TCall> source, EventSubscription<TCall> subscription)
            where TCall : EventCall
        {
            return new TrackedSubscription(subscription, source.Name, () => source.Options);
        }
    }

    /// <summary>
    /// Holds a subscription for the delegates created before the event created it: the handler that runs calls on the renderer
    /// and the exception route. Until it is filled, the subscription counts as active, because a call can only arrive once the
    /// event has registered it.
    /// </summary>
    private sealed class SubscriptionSlot
    {
        private IEventSubscription? _subscription;

        /// <summary>
        /// The subscription, or <see langword="null"/> until <see cref="Fill"/> runs.
        /// </summary>
        public IEventSubscription? Subscription => Volatile.Read(ref _subscription);

        /// <summary>
        /// <see langword="true"/> once the subscription was removed by any route; calls still waiting for the renderer are then dropped.
        /// </summary>
        public bool WasRemoved => Subscription is { IsActive: false };

        public void Fill(IEventSubscription subscription)
        {
            Volatile.Write(ref _subscription, subscription);
        }
    }

    /// <summary>
    /// The exception handler the core calls for one subscription. It moves the exception to the component: to the
    /// subscription's own exception handler on the renderer, or else to the nearest <c>ErrorBoundary</c>. An exception that
    /// cannot reach the component (it was disposed, or its renderer is gone) goes to the event's own default exception handler.
    /// </summary>
    /// <remarks>
    /// The route has to exist before the core creates the subscription, so the subscription arrives later through the
    /// <see cref="SubscriptionSlot"/> it shares with the handler. Its id is only reported when the exception cannot reach the
    /// component; a raise that fails in the moment between registration and filling the slot reports no subscription id. For
    /// the same reason the event's options are read from the event the component subscribed through, not from the subscription.
    /// </remarks>
    private sealed class ExceptionRoute<TCall>
        where TCall : EventCall
    {
        private readonly ComponentEventSubscriptions _owner;
        private readonly IIntercomEvent<TCall> _source;
        private readonly Action<Exception, TCall>? _subscriptionExceptionHandler;
        private readonly bool _renderAfterHandler;
        private readonly SubscriptionSlot _subscriptionSlot;

        public ExceptionRoute(ComponentEventSubscriptions owner, IIntercomEvent<TCall> source, Action<Exception, TCall>? subscriptionExceptionHandler,
            bool renderAfterHandler, SubscriptionSlot subscriptionSlot)
        {
            _owner = owner;
            _source = source;
            _subscriptionExceptionHandler = subscriptionExceptionHandler;
            _renderAfterHandler = renderAfterHandler;
            _subscriptionSlot = subscriptionSlot;
        }

        //the core calls exception handlers synchronously, so the delivery is started here and observed by DeliverAsync
        public void Handle(Exception exception, TCall call)
        {
            _ = DeliverAsync(exception, call);
        }

        //never throws
        private async Task DeliverAsync(Exception exception, TCall call)
        {
            bool delivered;
            if (_subscriptionExceptionHandler is not null)
            {
                delivered = await _owner.TryRunExceptionHandlerOnRendererAsync(_subscriptionExceptionHandler, exception, call, _renderAfterHandler);
            }
            else
            {
                delivered = await _owner.TryDispatchToComponentAsync(exception);
            }
            if (delivered)
            {
                return;
            }
            var subscriptionId = _subscriptionSlot.Subscription?.Id;
            var failure = new EventHandlerFailure(exception, EventFailureStage.Handler, call.Metadata.EventName, subscriptionId, call);
            ReportToEventExceptionHandler(failure, ReadEventOptions);
        }

        private EventingOptions ReadEventOptions()
        {
            return _source.Options;
        }
    }
}
