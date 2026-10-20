namespace IntercomEventing;

/// <summary>
/// Asks the IntercomEventing source generator to give the containing type a native C# <see langword="event"/> for this event
/// field, so subscribers can write <c>producer.UserCreated += handler</c>. With a <see langword="private"/> field, the public
/// members offer subscribers no way to raise or dispose the event.
/// </summary>
/// <remarks>
/// <para>For each marked field the generator adds two public members to the type that declares it:</para>
/// <list type="bullet">
/// <item><description><c>event Func&lt;TCall, Task&gt; &lt;Name&gt;</c>, whose <c>add</c> and <c>remove</c> forward to the field's
/// <see cref="IntercomEventBase{TCall}.Raised"/>: <c>+=</c> gives no handle and takes no hooks, and <c>-=</c> removes the most
/// recent equal delegate and starts that subscription's <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook, if it
/// has one, in the background.</description></item>
/// <item><description><c>IIntercomEvent&lt;TCall&gt; &lt;Name&gt;Event</c>, which returns the field, for <c>Subscribe</c>,
/// <c>SubscribeAsync</c> and the event's options. It is the event object itself typed as the subscribe-only interface, so it
/// hides the raise and dispose members rather than making them unreachable: code that casts it back to the event type can
/// call them.</description></item>
/// </list>
/// <para>An <see cref="ObsoleteAttribute"/> or <c>ExperimentalAttribute</c> on the field is copied to both members, so their
/// users see the same diagnostic. A call type that uses an obsolete or experimental type (such as a deprecated payload) gives
/// that diagnostic at the field, where it can be suppressed; the generated file does not repeat it.</para>
/// <para>The field:</para>
/// <list type="bullet">
/// <item><description>Has an IntercomEvent kind as its type: <see cref="IntercomEventBase{TCall}"/> or a class deriving from it,
/// such as <see cref="IntercomEvent{TPayload}"/>, <see cref="ThresholdEvent{TValue,TPayload}"/> or a custom kind.</description></item>
/// <item><description>May have any accessibility. <see langword="private"/> <see langword="readonly"/> is recommended, so only the
/// producer holds the event as a type that can raise it, and the event is never replaced; a field that is not
/// <see langword="readonly"/> still generates, with warning INTEV005.</description></item>
/// <item><description>May be <see langword="static"/>; the generated members are then <see langword="static"/> too.</description></item>
/// <item><description>Is declared in a <see langword="partial"/> class, struct, record or record struct, and every type around
/// it is <see langword="partial"/> too. A struct producer must be created by a constructor that assigns the event: in a
/// <see langword="default"/> struct, or an array element that was never assigned, the field is <see langword="null"/>, so the
/// view property returns <see langword="null"/> and <c>+=</c> throws <see cref="NullReferenceException"/>. A record producer
/// includes the view property in its generated <c>ToString</c> and <c>PrintMembers</c>.</description></item>
/// <item><description>Has a call type that the public members can expose: the call type and every type in it, such as the
/// payload, must be visible wherever the declaring type is (INTEV009), and reachable through <c>global::</c>, so not only through
/// an <c>extern alias</c> (INTEV012). A public producer needs a public payload; an internal producer may use an internal
/// one.</description></item>
/// </list>
/// <para>Naming: without <see cref="Name"/>, <c>&lt;Name&gt;</c> comes from the field name. An <c>m_</c> or <c>s_</c> prefix, or else
/// every leading underscore, is removed, the first letter is upper-cased, and a trailing <c>Event</c> is removed when a valid
/// name remains: <c>_userCreated</c>, <c>m_userCreated</c> and <c>_userCreatedEvent</c> all become <c>UserCreated</c>, while
/// <c>_event</c> becomes <c>Event</c>. A <see cref="Name"/> is used exactly as written.</para>
/// <para>Diagnostics (errors generate nothing for the field, the warning still generates):</para>
/// <list type="bullet">
/// <item><term>INTEV001</term><description>The declaring type, or a type around it, is not <see langword="partial"/>.</description></item>
/// <item><term>INTEV002</term><description>The field is in an interface, enum or file-local type, or in a type nested in one.</description></item>
/// <item><term>INTEV003</term><description>The field type is not an IntercomEvent kind; a type parameter is not accepted
/// even when it is constrained to one.</description></item>
/// <item><term>INTEV004</term><description>The field type is nullable.</description></item>
/// <item><term>INTEV005</term><description>Warning: the field is not <see langword="readonly"/>, so assigning a new event would
/// orphan existing subscribers.</description></item>
/// <item><term>INTEV006</term><description>The event name is not a valid C# identifier or is a keyword.</description></item>
/// <item><term>INTEV007</term><description>A generated name is already used by the declaring type, one of its type parameters,
/// a declared or inherited member, or a member generated for another field; or a generated accessor name (<c>add_</c>,
/// <c>remove_</c>, <c>get_</c> or <c>set_</c>) is taken by a declared method with the accessor's parameters, by another declared
/// member, or by a member generated for another field.</description></item>
/// <item><term>INTEV008</term><description>Two fields in one type generate the same member name.</description></item>
/// <item><term>INTEV009</term><description>The call type, or a type in it, is less accessible than the generated members.</description></item>
/// <item><term>INTEV010</term><description>The field is <see langword="const"/>.</description></item>
/// <item><term>INTEV011</term><description><c>[field: IntercomEvent]</c> is on a property, event or parameter instead of a
/// declared field.</description></item>
/// <item><term>INTEV012</term><description>The call type uses a type from an assembly that is referenced only through an
/// <c>extern alias</c>.</description></item>
/// </list>
/// <para>Configure a rule's severity with <c>dotnet_diagnostic.INTEVxxx.severity</c> in <c>.editorconfig</c>, or suppress it
/// with <c>#pragma warning disable</c>. Category-wide and bulk settings (<c>dotnet_analyzer_diagnostic.*</c>) reach only
/// INTEV011: the other rules are reported by the source generator, and those settings apply to analyzers only.</para>
/// </remarks>
/// <example>
/// <code>
/// public sealed partial class UserRegistry
/// {
///     [IntercomEvent]
///     private readonly IntercomEvent&lt;UserCreated&gt; _userCreated = new();
///
///     [IntercomEvent]
///     private readonly IntercomEvent&lt;UserDeleted&gt; _userDeletedEvent = new();
///
///     [IntercomEvent(Name = "LoginsExceeded")]
///     private readonly ThresholdEvent&lt;int, LoginAttempt&gt; _failedLogins = new(threshold: 3);
///
///     public async Task CreateAsync(UserCreated user)
///     {
///         await _userCreated.RaiseAsync(user, caller: this);
///     }
/// }
///
/// // generated members (each event's add and remove forward to the field's Raised):
/// //   public event Func&lt;EventCall&lt;UserCreated&gt;, Task&gt; UserCreated
/// //   public IIntercomEvent&lt;EventCall&lt;UserCreated&gt;&gt; UserCreatedEvent =&gt; _userCreated;
/// //   public event Func&lt;EventCall&lt;UserDeleted&gt;, Task&gt; UserDeleted
/// //   public IIntercomEvent&lt;EventCall&lt;UserDeleted&gt;&gt; UserDeletedEvent =&gt; _userDeletedEvent;
/// //   public event Func&lt;ThresholdEventCall&lt;int, LoginAttempt&gt;, Task&gt; LoginsExceeded
/// //   public IIntercomEvent&lt;ThresholdEventCall&lt;int, LoginAttempt&gt;&gt; LoginsExceededEvent =&gt; _failedLogins;
///
/// registry.UserCreated += OnUserCreatedAsync;
/// using var subscription = registry.UserCreatedEvent.Subscribe(call => Console.WriteLine(call.Payload.Name));
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class IntercomEventAttribute : Attribute
{
    /// <summary>
    /// The name of the generated event, used exactly as written; the view property is this name plus <c>Event</c>. When
    /// <see langword="null"/>, the name is derived from the field name: <c>_userCreated</c> and <c>_userCreatedEvent</c> both
    /// become <c>UserCreated</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// // generates the event Joined and the view property JoinedEvent
    /// [IntercomEvent(Name = "Joined")]
    /// private readonly IntercomEvent&lt;UserJoined&gt; _userJoinedEvent = new();
    /// </code>
    /// </example>
    public string? Name { get; init; }
}
