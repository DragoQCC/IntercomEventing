using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor;

/// <summary>
/// Subscribes from markup: while the listener is in the render tree, every call of <see cref="Event"/> is passed to
/// <see cref="OnRaised"/> on the renderer. Renders nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="OnRaised"/> is an <see cref="EventCallback{TValue}"/>, so the component that supplied it
/// re-renders afterwards without calling <c>StateHasChanged</c>.</description></item>
/// <item><description>The subscription starts when the listener is first rendered, moves to the new event when
/// <see cref="Event"/> changes (<see langword="null"/> unsubscribes), and ends when the listener leaves the render tree.
/// Nothing is subscribed during static rendering or prerendering.</description></item>
/// <item><description>Name <typeparamref name="TCall"/> when <see cref="OnRaised"/> is a method group: Razor cannot infer it
/// from a method group. It is inferred when <see cref="OnRaised"/> is a lambda with a typed parameter.</description></item>
/// <item><description>An exception thrown by <see cref="OnRaised"/> goes to the nearest <c>ErrorBoundary</c>; without one it is
/// unhandled and ends a Blazor Server circuit, like any exception from a component's event handler.</description></item>
/// <item><description>Setting <see cref="Event"/> to an event that has been disposed (<see cref="ObjectDisposedException"/>),
/// or to one that allows a single subscriber and already has one (<see cref="InvalidOperationException"/>), fails the
/// parameter update, and the listener keeps listening to the event it had. Like any exception while setting parameters, the
/// nearest <c>ErrorBoundary</c> shows it; without one it ends a Blazor Server circuit.</description></item>
/// </list>
/// </remarks>
/// <typeparam name="TCall">The event's call type, for example <c>EventCall&lt;OrderPlaced&gt;</c>.</typeparam>
/// <example>
/// <code>
/// &lt;EventListener TCall="EventCall&lt;OrderPlaced&gt;" Event="Orders.OrderPlaced" OnRaised="OnOrderPlaced" /&gt;
///
/// @if (_showAlerts)
/// {
///     @* subscribed only while this branch is rendered; TCall is inferred from the typed lambda *@
///     &lt;EventListener Event="Security.LoginsExceeded" OnRaised="(ThresholdEventCall&lt;int, LoginAttempt&gt; call) =&gt; ShowAlert(call)" /&gt;
/// }
/// </code>
/// </example>
public sealed class EventListener<TCall> : ComponentBase, IAsyncDisposable
    where TCall : EventCall
{
    private readonly ComponentEventSubscriptions _subscriptions;
    private EventSubscription<TCall>? _subscription;

    /// <summary>
    /// The event to listen to. Replacing it moves the subscription to the new event; <see langword="null"/> unsubscribes.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public IIntercomEvent<TCall>? Event { get; set; }

    /// <summary>
    /// Invoked on the renderer for each call; the component that supplied it re-renders afterwards.
    /// </summary>
    [Parameter]
    public EventCallback<TCall> OnRaised { get; set; }

    /// <summary>
    /// Creates the listener.
    /// </summary>
    public EventListener()
    {
        //the listener renders nothing; OnRaised (an EventCallback) re-renders the component that supplied it
        _subscriptions = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
    }

    /// <summary>
    /// Removes the subscription when the listener leaves the render tree. Never throws.
    /// </summary>
    /// <returns>A task that completes when the subscription is removed.</returns>
    public ValueTask DisposeAsync()
    {
        return _subscriptions.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _subscription = _subscriptions.Resubscribe(_subscription, Event, ForwardToOnRaisedAsync, renderAfterHandler: false);
    }

    /// <inheritdoc />
    protected override bool ShouldRender()
    {
        return false;
    }

    //reads OnRaised at call time, so a new callback from the parent takes effect without resubscribing
    private Task ForwardToOnRaisedAsync(TCall call)
    {
        return OnRaised.InvokeAsync(call);
    }
}
