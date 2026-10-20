using System.Text;
using IntercomEventing.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace IntercomEventing.Generators;

/// <summary>
/// Generates a native C# <see langword="event"/> and a subscribe-only view property for every field marked with
/// <c>[IntercomEvent]</c>, and reports the diagnostics in <see cref="IntercomEventDiagnostics"/> (INTEV001 to INTEV010 and
/// INTEV012) for fields it cannot generate.
/// </summary>
/// <remarks>
/// <para>Pipeline: <c>ForAttributeWithMetadataName</c> parses each attributed field into an <see cref="IntercomEventFieldResult"/>
/// (plain values, no symbols or syntax), which then splits into a diagnostics output and a source output with one file per
/// field. An edit that leaves a field's result equal keeps that field's file cached.</para>
/// <para>The generated members are described in <c>IntercomEventing.IntercomEventAttribute</c>.</para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class IntercomEventGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Metadata name of the attribute that marks a field for generation.
    /// </summary>
    public const string AttributeMetadataName = "IntercomEventing.IntercomEventAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fieldResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                predicate: static (node, _) => IsAttributedFieldCandidate(node),
                transform: static (attributeContext, cancellationToken) => IntercomEventFieldParser.Parse(attributeContext, cancellationToken))
            .WithTrackingName(IntercomEventTrackingNames.FieldResults);

        var diagnostics = fieldResults
            .SelectMany(static (result, _) => result.Diagnostics)
            .WithTrackingName(IntercomEventTrackingNames.Diagnostics);
        //the compilation is used only to place each diagnostic back in its syntax tree (see LocationInfo.ToLocation);
        //this step runs per diagnostic, so it costs nothing while every field is valid
        context.RegisterSourceOutput(diagnostics.Combine(context.CompilationProvider), static (sourceContext, diagnosticAndCompilation) =>
        {
            var (diagnostic, compilation) = diagnosticAndCompilation;
            sourceContext.ReportDiagnostic(diagnostic.ToDiagnostic(compilation));
        });

        var generatableFields = fieldResults
            .Where(static result => result.Field is not null)
            .Select(static (result, _) => result.Field!)
            .WithTrackingName(IntercomEventTrackingNames.GeneratableFields);
        context.RegisterSourceOutput(generatableFields, static (sourceContext, field) =>
        {
            var source = IntercomEventSourceWriter.Write(field);
            sourceContext.AddSource(field.HintName, SourceText.From(source, Encoding.UTF8));
        });
    }

    //an attribute on a field declaration targets each of its variables; enum members are fields too and are let through so
    //the parser can report them instead of silently ignoring the attribute
    private static bool IsAttributedFieldCandidate(SyntaxNode node)
    {
        var isFieldVariable = node is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax } };
        var isEnumMember = node is EnumMemberDeclarationSyntax;
        return isFieldVariable || isEnumMember;
    }
}

/// <summary>
/// Names of the tracked pipeline steps, so tests can check that an unrelated edit leaves them cached.
/// </summary>
internal static class IntercomEventTrackingNames
{
    public const string FieldResults = "IntercomEventFieldResults";
    public const string Diagnostics = "IntercomEventDiagnostics";
    public const string GeneratableFields = "IntercomEventGeneratableFields";
}
