namespace IntercomEventing;

/// <summary>
/// The library-owned record of one raise of an event. Every handler of that raise receives the same instance, and it
/// never changes after it is created.
/// </summary>
/// <remarks>
/// Each event kind has its own call type that derives from this class (for example <see cref="EventCall{TPayload}"/>
/// or <see cref="ThresholdEventCall{TValue,TPayload}"/>). A handler written against a base call type can be attached to
/// any event whose call type derives from it, because <see cref="Func{T,TResult}"/> is contravariant.
/// </remarks>
/// <example>
/// <code>
/// // one logging handler shared by every event, whatever its payload
/// static Task LogAnyCall(EventCall call)
/// {
///     Console.WriteLine($"{call.Metadata.EventName} raised at {call.Metadata.RaisedAt:O}");
///     return Task.CompletedTask;
/// }
///
/// registry.UserCreated.Raised += LogAnyCall;
/// monitor.PowerChanged.Raised += LogAnyCall;
/// </code>
/// </example>
public abstract class EventCall
{
    /// <summary>
    /// Identifiers, timestamp and caller of this raise.
    /// </summary>
    public EventMetadata Metadata { get; }

    /// <summary>
    /// Cancelled when the code that raised the event cancels the raise, or when the event is disposed.
    /// Long-running handlers (including handlers that outlived <see cref="EventingOptions.StartNextEventHandlerAfter"/>)
    /// should observe it.
    /// </summary>
    /// <remarks>
    /// <para>A handler that ends with <see cref="OperationCanceledException"/> after this token was cancelled counts as
    /// cancelled, not failed: the exception is not passed to the subscription's exception handler or to
    /// <see cref="EventingOptions.DefaultExceptionHandler"/>.</para>
    /// <para>The token is guaranteed to be signalled only while the handlers of this raise are running. Once every one of them
    /// has finished, the library may release the source behind it, and the token then stays uncancelled. Work that a handler
    /// starts without awaiting it should use a token of its own.</para>
    /// </remarks>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Creates the shared part of an event call.
    /// </summary>
    /// <param name="metadata">Identifiers, timestamp and caller of this raise.</param>
    /// <param name="cancellationToken">Cancelled when the raise is cancelled by its caller or the event is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    protected EventCall(EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Metadata = metadata;
        CancellationToken = cancellationToken;
    }
}

/// <summary>
/// The call delivered to handlers of an <see cref="IntercomEvent{TPayload}"/>: the payload passed to
/// <see cref="IntercomEvent{TPayload}.RaiseAsync(TPayload, object?, CancellationToken)"/> plus the raise metadata.
/// The specialized call types of the other event kinds derive from it.
/// </summary>
/// <typeparam name="TPayload">The user-defined data carried by the event, usually a record.</typeparam>
/// <example>
/// <code>
/// registry.UserCreated.Subscribe(call =>
/// {
///     var (user, metadata) = call;
///     Console.WriteLine($"{user.Name} created (call {metadata.EventCallId})");
/// });
/// </code>
/// </example>
public class EventCall<TPayload> : EventCall
{
    /// <summary>
    /// The data passed to the raise. It is shared by every handler of this call, so prefer immutable payload types.
    /// </summary>
    public TPayload Payload { get; }

    /// <summary>
    /// Creates an event call. The library creates calls when an event is raised; construct one yourself only to unit test
    /// a handler.
    /// </summary>
    /// <param name="payload">The data passed to the raise.</param>
    /// <param name="metadata">Identifiers, timestamp and caller of the raise.</param>
    /// <param name="cancellationToken">Token exposed to handlers as <see cref="EventCall.CancellationToken"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var metadata = new EventMetadata(Guid.NewGuid(), "UserCreated", Guid.CreateVersion7(), DateTimeOffset.UtcNow, Caller: null);
    /// await handler.OnUserCreatedAsync(new EventCall&lt;UserCreated&gt;(new UserCreated("Ada", "ada@example.com"), metadata));
    /// </code>
    /// </example>
    public EventCall(TPayload payload, EventMetadata metadata, CancellationToken cancellationToken = default)
        : base(metadata, cancellationToken)
    {
        Payload = payload;
    }

    /// <summary>
    /// Splits the call into its payload and metadata.
    /// </summary>
    /// <param name="payload">Receives <see cref="Payload"/>.</param>
    /// <param name="metadata">Receives <see cref="EventCall.Metadata"/>.</param>
    /// <example>
    /// <code>
    /// var (user, metadata) = call;
    /// </code>
    /// </example>
    public void Deconstruct(out TPayload payload, out EventMetadata metadata)
    {
        payload = Payload;
        metadata = Metadata;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Metadata.EventName} call {Metadata.EventCallId}: {Payload}";
    }
}
