namespace IntercomEventing;

/// <summary>
/// Marks one run of user code that an event started and may later wait for: a handler invocation that can be moved to the
/// background, an unsubscribe hook started in the background, or a subscribe hook that can be abandoned by its caller.
/// </summary>
/// <remarks>
/// <para>The current scope flows with the async context (an <see cref="AsyncLocal{T}"/>) into that code and across its awaits.
/// <see cref="IntercomEventBase{TCall}.DisposeAsync"/> reads it to recognise background work that is its own caller, or that
/// its caller is running inside of, and skips waiting for that work: waiting would deadlock because the work is waiting for
/// <c>DisposeAsync</c>.</para>
/// <para>Scopes nest. A handler of event A that raises event B gives B's handlers a scope enclosed by A's handler scope,
/// so B's handler disposing A skips A's handler as well.</para>
/// <para>Because it is an <see cref="AsyncLocal{T}"/>, the scope also flows into work the user code starts without awaiting
/// (<c>Task.Run</c>, an un-awaited task). <c>DisposeAsync</c> called from that work therefore skips the handler that started
/// it, even though the handler is not waiting for it. This is documented on <c>DisposeAsync</c>; callers who need the wait
/// suppress the flow with <see cref="ExecutionContext.SuppressFlow"/>.</para>
/// </remarks>
internal sealed class EventCallbackScope
{
    private static readonly AsyncLocal<EventCallbackScope?> s_currentScope = new();

    /// <summary>
    /// The scope that was current when this one was created, or <see langword="null"/> for an outermost scope.
    /// </summary>
    public EventCallbackScope? EnclosingScope { get; }

    /// <summary>
    /// The scope of the user code running on the current async flow, or <see langword="null"/> outside any tracked handler or hook.
    /// </summary>
    public static EventCallbackScope? Current => s_currentScope.Value;

    private EventCallbackScope(EventCallbackScope? enclosingScope)
    {
        EnclosingScope = enclosingScope;
    }

    /// <summary>
    /// Creates a scope nested inside the current one. It becomes current only through <see cref="Enter"/>.
    /// </summary>
    public static EventCallbackScope CreateNested()
    {
        return new EventCallbackScope(s_currentScope.Value);
    }

    /// <summary>
    /// Makes <paramref name="scope"/> current until the returned activation is disposed. Code started meanwhile (a handler,
    /// a hook) captures it and keeps it across its awaits after the activation ends.
    /// </summary>
    public static Activation Enter(EventCallbackScope scope)
    {
        return new Activation(scope);
    }

    /// <summary>
    /// Clears the current scope until the returned activation is disposed, for background work (a debounce wait) that must
    /// not count as part of the handler that happened to start it. A timed event's schedule suppresses the whole
    /// <see cref="ExecutionContext"/> flow instead, which clears this scope too.
    /// </summary>
    public static Activation EnterDetached()
    {
        return new Activation(null);
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="callingScope"/> is this scope or is nested (at any depth) inside it, meaning
    /// the calling code runs as part of the work this scope marks.
    /// </summary>
    public bool IsSameAsOrEncloses(EventCallbackScope? callingScope)
    {
        for (var scope = callingScope; scope is not null; scope = scope.EnclosingScope)
        {
            if (ReferenceEquals(scope, this))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Restores the previously current scope when disposed; use it with a <see langword="using"/> statement.
    /// </summary>
    internal readonly struct Activation : IDisposable
    {
        private readonly EventCallbackScope? _previousScope;

        public Activation(EventCallbackScope? scopeToMakeCurrent)
        {
            _previousScope = s_currentScope.Value;
            //AsyncLocal ignores a write of the value it already holds, so detaching outside any scope costs nothing
            s_currentScope.Value = scopeToMakeCurrent;
        }

        public void Dispose()
        {
            s_currentScope.Value = _previousScope;
        }
    }
}
