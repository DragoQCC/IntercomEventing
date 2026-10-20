namespace IntercomEventing.Generators.Models;

/// <summary>
/// Everything the source writer needs to generate the members for one valid <c>[IntercomEvent]</c> field, as plain strings
/// so an unchanged field compares equal across runs and its generated file stays cached.
/// </summary>
/// <param name="HintName">Unique name of the generated file.</param>
/// <param name="NamespaceName">Namespace of the containing type without <c>global::</c>; <see langword="null"/> for the global namespace.</param>
/// <param name="ContainingTypes">The containing type and every type around it, outermost first.</param>
/// <param name="EventName">Name of the generated event; the view property is this name plus <c>Event</c>.</param>
/// <param name="CallTypeName">Fully qualified <c>TCall</c> of the field's <c>IntercomEventBase&lt;TCall&gt;</c>.</param>
/// <param name="FieldAccessExpression">Expression that reads the field from the generated members, such as <c>this._userCreated</c>.</param>
/// <param name="IsStatic">Whether the field, and so the generated members, are static.</param>
/// <param name="ExampleReceiver">What the generated documentation example subscribes on: a variable name, or the type name for static members.</param>
/// <param name="ForwardedAttributes">The field's <c>[Obsolete]</c> and <c>[Experimental]</c> attributes as source text without
/// brackets, such as <c>global::System.ObsoleteAttribute("Use Renamed instead")</c>; both generated members repeat them.</param>
/// <param name="SuppressedDiagnosticIds">Diagnostic IDs that naming the call type reports in the generated file because a type
/// in it is obsolete or experimental; the user sees them at the field, so the generated file disables them.</param>
internal sealed record IntercomEventFieldModel(
    string HintName,
    string? NamespaceName,
    EquatableArray<ContainingTypeModel> ContainingTypes,
    string EventName,
    string CallTypeName,
    string FieldAccessExpression,
    bool IsStatic,
    string ExampleReceiver,
    EquatableArray<string> ForwardedAttributes,
    EquatableArray<string> SuppressedDiagnosticIds);

/// <summary>
/// One type the generated members are nested in, as its partial declaration header needs it.
/// </summary>
/// <param name="Keyword">The declaration keyword: <c>class</c>, <c>struct</c>, <c>record</c> or <c>record struct</c>.</param>
/// <param name="DeclarationName">The type name with its type parameters, such as <c>Box&lt;T&gt;</c>.</param>
internal sealed record ContainingTypeModel(string Keyword, string DeclarationName);

/// <summary>
/// What parsing one <c>[IntercomEvent]</c> field produced: the model to generate from, the diagnostics to report, or both
/// (a field with only warnings is still generated).
/// </summary>
/// <param name="Field">The model; <see langword="null"/> when an error prevents generation.</param>
/// <param name="Diagnostics">Diagnostics for the field, in the order they were found.</param>
internal sealed record IntercomEventFieldResult(IntercomEventFieldModel? Field, EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>
    /// Nothing to generate and nothing to report, for fields the compiler already reports errors on.
    /// </summary>
    public static IntercomEventFieldResult Nothing { get; } = new(null, EquatableArray<DiagnosticInfo>.Empty);
}
