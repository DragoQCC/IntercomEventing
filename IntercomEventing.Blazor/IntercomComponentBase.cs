using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor;

/// <summary>
/// A <see cref="ComponentBase"/> with built-in <see cref="Events"/>: handlers run on the renderer, the component re-renders
/// after each one, handler exceptions reach the nearest <c>ErrorBoundary</c>, and every subscription is removed when the
/// component is disposed. No dispose code is needed in the component.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Subscribe in <c>OnInitialized</c> or <c>OnInitializedAsync</c>, not in the constructor. For an event that
/// arrives as a parameter or cascading value, call <c>Events.Resubscribe</c> from <c>OnParametersSet</c>.</description></item>
/// <item><description>During static rendering and prerendering nothing is subscribed; the interactive instance subscribes.</description></item>
/// <item><description>A handler exception without an <c>ErrorBoundary</c> is unhandled, like any exception from a component's
/// event handler: it ends a Blazor Server circuit and shows the error UI on WebAssembly. Pass an exception handler to
/// <c>Subscribe</c> to handle it in the component instead. A handler whose task ends cancelled is not an exception, as with a
/// UI event handler, so cancelling a token in <see cref="DisposeAsyncCore"/> is safe.</description></item>
/// <item><description>Cleanup: override <see cref="DisposeAsyncCore"/>, or implement <see cref="IDisposable"/>
/// (<c>@implements IDisposable</c>); both run after the subscriptions are removed.</description></item>
/// <item><description>Do not declare <c>DisposeAsync</c> in a derived component; the compiler reports only warning CS0108.
/// With <c>@implements IAsyncDisposable</c> it replaces <see cref="DisposeAsync"/> and the subscriptions are never removed;
/// without it, the renderer never calls it.</description></item>
/// <item><description>If the component cannot derive from this class, use <see cref="ComponentEventSubscriptions"/>
/// directly.</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// @* UserFeed.razor *@
/// @inherits IntercomComponentBase
/// &lt;ul&gt;
///     @foreach (var name in RecentNames)
///     {
///         &lt;li&gt;@name&lt;/li&gt;
///     }
/// &lt;/ul&gt;
///
/// // UserFeed.razor.cs
/// public partial class UserFeed
/// {
///     private readonly List&lt;string&gt; _recentNames = [];
///
///     [Inject]
///     private UserDirectory Users { get; set; } = default!;
///
///     protected IReadOnlyList&lt;string&gt; RecentNames => _recentNames;
///
///     protected override void OnInitialized()
///     {
///         Events.Subscribe(Users.UserJoined, call => _recentNames.Insert(0, call.Payload.Name));
///     }
/// }
/// </code>
/// </example>
public abstract class IntercomComponentBase : ComponentBase, IAsyncDisposable
{
    /// <summary>
    /// The component's subscriptions. Each handler runs on the renderer and re-renders the component; all of them are removed
    /// when the component is disposed.
    /// </summary>
    /// <example>
    /// <code>
    /// protected override void OnInitialized()
    /// {
    ///     Events.Subscribe(Orders.OrderPlaced, call => _orderCount++);
    /// }
    /// </code>
    /// </example>
    protected ComponentEventSubscriptions Events { get; }

    /// <summary>
    /// Creates the component and its subscription owner.
    /// </summary>
    protected IntercomComponentBase()
    {
        //the delegates are only invoked once the renderer has attached the component (when a handler runs or a subscription is made)
        Events = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
    }

    /// <summary>
    /// Called by the renderer when the component is removed: removes every subscription, runs <see cref="DisposeAsyncCore"/>,
    /// then calls <see cref="IDisposable.Dispose"/> when the derived component implements <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// <para>The subscriptions go first, so no handler starts while the component releases what handlers use, and unsubscribe
    /// hooks can still use the component's resources. Removing them never throws.</para>
    /// <para>The renderer calls only <c>DisposeAsync</c> on a component that implements <see cref="IAsyncDisposable"/>, so this
    /// method calls a derived component's <see cref="IDisposable.Dispose"/> itself.</para>
    /// </remarks>
    /// <returns>A task that completes when the subscriptions are removed and the component's own cleanup has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync();
        await DisposeAsyncCore();
        //without this, a derived component's @implements IDisposable would silently never run
        if (this is IDisposable disposableComponent)
        {
            disposableComponent.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Override to release the component's own resources; the event subscriptions have already been removed.
    /// </summary>
    /// <returns>A task that completes when the component's resources are released.</returns>
    /// <example>
    /// <code>
    /// protected override async ValueTask DisposeAsyncCore()
    /// {
    ///     await _hubConnection.DisposeAsync();
    /// }
    /// </code>
    /// </example>
    protected virtual ValueTask DisposeAsyncCore()
    {
        return ValueTask.CompletedTask;
    }
}
