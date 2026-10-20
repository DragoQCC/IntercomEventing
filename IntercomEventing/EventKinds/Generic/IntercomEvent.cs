namespace IntercomEventing;

/// <summary>
/// An event that carries a <typeparamref name="TPayload"/> to its subscribers each time it is raised.
/// No event class or call class of your own is needed: the payload is any type (usually a record) and handlers receive
/// <see cref="EventCall{TPayload}"/>.
/// </summary>
/// <remarks>
/// <para>Give the event its domain name through the member that exposes it (<c>UserCreated</c> below). When you also want a
/// domain <em>type</em> (to register it in DI, add raise helpers or keep an existing type name), derive from it:
/// <c>public sealed class UserCreatedEvent : IntercomEvent&lt;UserCreated&gt;;</c>. Handlers are unaffected because the call
/// type stays <see cref="EventCall{TPayload}"/>. Events implement <see cref="IAsyncDisposable"/> only, so a container that
/// holds one must be disposed with <c>DisposeAsync</c> (see <see cref="IntercomEventBase{TCall}.DisposeAsync"/>).</para>
/// <para>Anyone holding the event can raise it. To expose subscribe-only access, keep the event in a private field and expose
/// it as <see cref="IIntercomEvent{TCall}"/> (<c>IIntercomEvent&lt;EventCall&lt;UserCreated&gt;&gt;</c>), or mark the field with
/// <see cref="IntercomEventAttribute"/> to generate a native C# event for it.</para>
/// <para>Concurrent raises are not serialized: they are delivered concurrently and their handlers may interleave. Each raise
/// gets its own call, so handlers never see another raise's payload.</para>
/// </remarks>
/// <typeparam name="TPayload">The data passed with each raise.</typeparam>
/// <example>
/// <code>
/// public sealed record UserCreated(string Name, string Email);
///
/// public sealed class UserRegistry
/// {
///     public IntercomEvent&lt;UserCreated&gt; UserCreated { get; } = new();
///
///     public async Task CreateAsync(UserCreated user)
///     {
///         // ... persist the user ...
///         await UserCreated.RaiseAsync(user, caller: this);
///     }
/// }
///
/// // subscriber: the call type is inferred, so a handler for the wrong call type does not compile
/// registry.UserCreated.Raised += call => Console.Out.WriteLineAsync($"Welcome {call.Payload.Name}");
/// using var subscription = registry.UserCreated.Subscribe(call => Console.WriteLine($"Welcome {call.Payload.Name}"));
/// </code>
/// </example>
public class IntercomEvent<TPayload> : IntercomEventBase<EventCall<TPayload>>
{
    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    /// <example>
    /// <code>
    /// var orderPlaced = new IntercomEvent&lt;OrderPlaced&gt;(EventingOptions.Default with { DispatchMode = DispatchMode.Sequential });
    /// </code>
    /// </example>
    public IntercomEvent(EventingOptions? options = null)
        : base(options)
    {
    }

    /// <summary>
    /// Raises the event with <paramref name="payload"/>. Each raise gets its own call, so concurrent raises never share data.
    /// </summary>
    /// <remarks>
    /// With no subscribers this returns a completed task without allocating.
    /// </remarks>
    /// <param name="payload">The data every handler of this raise receives as <see cref="EventCall{TPayload}.Payload"/>.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>, often <c>this</c>.</param>
    /// <param name="cancellationToken">Stops starting further handlers and is visible to handlers as <see cref="EventCall.CancellationToken"/>.</param>
    /// <returns>A task that completes when every handler finished or was moved to the background.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before every handler started.</exception>
    /// <example>
    /// <code>
    /// await registry.UserCreated.RaiseAsync(new UserCreated("Ada", "ada@example.com"), caller: this);
    /// </code>
    /// </example>
    public Task RaiseAsync(TPayload payload, object? caller = null, CancellationToken cancellationToken = default)
    {
        return DispatchAsync(
            payload,
            static (raisedPayload, metadata, callCancellationToken) => new EventCall<TPayload>(raisedPayload, metadata, callCancellationToken),
            caller,
            cancellationToken);
    }
}

/// <summary>
/// An event without a payload: subscribers only learn that it happened (plus <see cref="EventMetadata"/>).
/// </summary>
/// <example>
/// <code>
/// public IntercomEvent CacheCleared { get; } = new();
///
/// cacheService.CacheCleared.Subscribe(call => Console.WriteLine($"cache cleared at {call.Metadata.RaisedAt}"));
/// await CacheCleared.RaiseAsync(caller: this);
/// </code>
/// </example>
public class IntercomEvent : IntercomEvent<NoPayload>
{
    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    /// <example>
    /// <code>
    /// var cacheCleared = new IntercomEvent();
    /// </code>
    /// </example>
    public IntercomEvent(EventingOptions? options = null)
        : base(options)
    {
    }

    /// <summary>
    /// Raises the event.
    /// </summary>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <param name="cancellationToken">Stops starting further handlers.</param>
    /// <returns>A task that completes when every handler finished or was moved to the background.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before every handler started.</exception>
    /// <example>
    /// <code>
    /// await CacheCleared.RaiseAsync(caller: this);
    /// </code>
    /// </example>
    public Task RaiseAsync(object? caller = null, CancellationToken cancellationToken = default)
    {
        return RaiseAsync(NoPayload.Value, caller, cancellationToken);
    }
}
