using Microsoft.CodeAnalysis;

namespace IntercomEventing.Generators;

/// <summary>
/// The diagnostics for <c>[IntercomEvent]</c>: <see cref="IntercomEventGenerator"/> reports INTEV001 to INTEV010 and
/// INTEV012, and <see cref="IntercomEventBackingFieldAnalyzer"/> reports INTEV011. Every ID must also be listed in
/// <c>AnalyzerReleases.Unshipped.md</c>, and an ID never changes meaning once shipped.
/// </summary>
/// <remarks>
/// <para>Errors stop generation for the field, so a misused attribute is reported once at the field instead of as compiler
/// errors inside the generated file. Warnings leave generation on. A field the compiler already rejects (a type that does not
/// bind, a duplicate name, an instance field in a static class, a file-local payload) generates nothing and gets no INTEV
/// diagnostic, for the same reason.</para>
/// <para>Severity settings: a generator diagnostic follows <c>dotnet_diagnostic.INTEVxxx.severity</c> and <c>#pragma</c>, but
/// not the category-wide or bulk <c>dotnet_analyzer_diagnostic.*</c> settings, which the compiler applies to analyzer
/// diagnostics only. Of these rules only INTEV011 comes from an analyzer.</para>
/// </remarks>
internal static class IntercomEventDiagnostics
{
    //one category for every rule. Only analyzer diagnostics (INTEV011) follow dotnet_analyzer_diagnostic.category-IntercomEventing.severity;
    //the generator's rules are configured one ID at a time (dotnet_diagnostic.INTEV005.severity)
    private const string Category = "IntercomEventing";

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        id: "INTEV001",
        title: "Type containing an [IntercomEvent] field must be partial",
        messageFormat: "Type '{0}' must be declared partial so the members for [IntercomEvent] field '{1}' can be generated",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator adds the event and its Event property to the type that declares the field. That type and every type containing it must be declared partial.");

    public static readonly DiagnosticDescriptor UnsupportedContainingType = new(
        id: "INTEV002",
        title: "[IntercomEvent] field is declared in an unsupported type",
        messageFormat: "Field '{0}' is declared in {1} '{2}', but [IntercomEvent] fields must be declared in a partial class, struct or record",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator can add members only to classes, structs, records and record structs. Interfaces, enums and file-local types (or types nested in them) cannot receive the generated members.");

    public static readonly DiagnosticDescriptor FieldTypeNotIntercomEvent = new(
        id: "INTEV003",
        title: "[IntercomEvent] field type is not an IntercomEvent kind",
        messageFormat: "Field '{0}' has type '{1}', {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated event forwards to the field's Raised event, so the field type must be IntercomEventBase<TCall> or a class derived from it, such as IntercomEvent<TPayload>, ThresholdEvent<TValue, TPayload> or a custom event kind. Interfaces and type parameters are not accepted, even a type parameter constrained to an event kind: the generator needs the event's own class type.");

    public static readonly DiagnosticDescriptor FieldTypeNullable = new(
        id: "INTEV004",
        title: "[IntercomEvent] field type must not be nullable",
        messageFormat: "Field '{0}' is declared as nullable '{1}'; remove the '?' and always assign an event to it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every += and -= on the generated event forwards to the field, and the generated Event property returns it as non-nullable. A field that can hold no event would make subscribing throw, so its type must be non-nullable.");

    public static readonly DiagnosticDescriptor FieldNotReadOnly = new(
        id: "INTEV005",
        title: "[IntercomEvent] field should be readonly",
        messageFormat: "Field '{0}' should be readonly: assigning a new event to it leaves every existing subscriber attached to the old event",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Handlers added through the generated event are stored in the event the field held at that moment. Assigning another event to the field silently orphans them, so declare the field readonly and assign it once.");

    public static readonly DiagnosticDescriptor InvalidEventName = new(
        id: "INTEV006",
        title: "Invalid [IntercomEvent] event name",
        messageFormat: "'{0}' is not a valid event name for field '{1}'; {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The event name must be a C# identifier that is not a keyword. Without IntercomEventAttribute.Name it is the field name without its m_ or s_ prefix (or else all of its leading underscores), with its first letter upper-cased and without a trailing Event when a valid name remains (_userJoinedEvent becomes UserJoined, _event stays Event).");

    public static readonly DiagnosticDescriptor GeneratedMemberConflict = new(
        id: "INTEV007",
        title: "Generated [IntercomEvent] member conflicts with an existing name",
        messageFormat: "The member '{0}' generated for field '{1}' conflicts with {2}; set IntercomEventAttribute.Name to choose another name",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator adds an event and a property named after it plus Event. Neither may reuse the name of the containing type, one of its type parameters, a member it declares (an indexer counts under its metadata name, usually Item), a non-generic member it inherits, or the accessor name of a member generated for another field. Their accessor names (add_ and remove_ for the event, get_ and set_ for the property) must stay free too: no declared method may have such a name together with the accessor's parameters, and no other declared or generated member may have such a name at all.");

    public static readonly DiagnosticDescriptor DuplicateGeneratedMember = new(
        id: "INTEV008",
        title: "Two [IntercomEvent] fields generate the same member",
        messageFormat: "Fields '{0}' and '{1}' both generate the member '{2}'; set IntercomEventAttribute.Name on one of them",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each [IntercomEvent] field generates an event and an Event property, so the generated names of all such fields in one type must differ. Neither field is generated until the conflict is resolved.");

    public static readonly DiagnosticDescriptor CallTypeLessAccessible = new(
        id: "INTEV009",
        title: "[IntercomEvent] call type is less accessible than the generated members",
        messageFormat: "The public members generated for field '{0}' expose '{1}', which is less accessible than '{2}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated event and Event property are public, so the call type and every type in it, such as the payload, must be at least as accessible as the type that declares the field. Make that type more accessible, or make the containing type less accessible.");

    public static readonly DiagnosticDescriptor ConstField = new(
        id: "INTEV010",
        title: "[IntercomEvent] field must not be const",
        messageFormat: "Field '{0}' is const, so it can only hold null; declare it readonly and assign it an event",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated members forward to the event the field holds, and a const field of a class type can only hold null, so the first += would throw. Declare a readonly (or static readonly) field and assign it an event.");

    public static readonly DiagnosticDescriptor AttributeOnBackingField = new(
        id: "INTEV011",
        title: "[IntercomEvent] must be applied to a declared field",
        messageFormat: "[IntercomEvent] cannot be applied with the field: target to {0}; declare a readonly field and put the attribute on it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The field: target places the attribute on the compiler-generated backing field of an auto-property, a field-like event or a record parameter, or nowhere when there is none. The generator only generates members for fields declared in source, so without this error the attribute would be ignored without any message.");

    public static readonly DiagnosticDescriptor CallTypeFromAliasedReference = new(
        id: "INTEV012",
        title: "[IntercomEvent] call type comes from a reference that has only an extern alias",
        messageFormat: "The members generated for field '{0}' would expose '{1}' from assembly '{2}', which is referenced only through the extern alias '{3}'; add 'global' to that reference's aliases, for example Aliases=\"global,{3}\"",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated file names every type with global::, so it cannot name a type from an assembly that is referenced only through an extern alias. Add global to that reference's aliases, or use a call type whose types come from other assemblies.");
}
