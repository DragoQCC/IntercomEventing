namespace IntercomEventing;

/// <summary>
/// Builds the default <see cref="IntercomEventBase{TCall}.Name"/> of an event from its runtime type.
/// </summary>
internal static class EventNameFormatter
{
    /// <summary>
    /// Turns <c>IntercomEvent`1[UserCreated]</c> into <c>IntercomEvent&lt;UserCreated&gt;</c>; non-generic types keep their plain name.
    /// </summary>
    public static string Describe(Type eventType)
    {
        if (!eventType.IsGenericType)
        {
            return eventType.Name;
        }
        var arityMarkerIndex = eventType.Name.IndexOf('`');
        var nameWithoutArity = arityMarkerIndex < 0 ? eventType.Name : eventType.Name[..arityMarkerIndex];
        var typeArgumentNames = eventType.GetGenericArguments().Select(Describe);
        return $"{nameWithoutArity}<{string.Join(", ", typeArgumentNames)}>";
    }
}
