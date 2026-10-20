using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IntercomEventing.Generators;

/// <summary>
/// Reports INTEV011 for <c>[field: IntercomEvent]</c> on anything but a field declaration: an auto-property, a field-like
/// event or a (record) parameter.
/// </summary>
/// <remarks>
/// The <c>field:</c> target puts the attribute on a compiler-generated backing field (or nowhere, when there is none).
/// <c>ForAttributeWithMetadataName</c> only finds attributes of the symbol a declaration declares, such as the property, so
/// <see cref="IntercomEventGenerator"/> never sees these and would ignore them without a message. The backing field of a
/// field-like event is not even listed among its type's members, so the attribute lists are inspected as syntax. This runs
/// as an ordinary analyzer rather than as a generator syntax provider that would walk every declaration on every edit.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IntercomEventBackingFieldAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(IntercomEventDiagnostics.AttributeOnBackingField);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            //a compilation without the library (or with two copies of it) has no attribute to look for
            var attributeType = compilationContext.Compilation.GetTypeByMetadataName(IntercomEventGenerator.AttributeMetadataName);
            if (attributeType is null)
            {
                return;
            }
            compilationContext.RegisterSyntaxNodeAction(nodeContext => AnalyzeAttributeList(nodeContext, attributeType), SyntaxKind.AttributeList);
        });
    }

    private static void AnalyzeAttributeList(SyntaxNodeAnalysisContext context, INamedTypeSymbol attributeType)
    {
        var attributeList = (AttributeListSyntax)context.Node;
        //syntax checks first, so only field:-targeted lists outside a field declaration are bound
        var targetsField = attributeList.Target?.Identifier.IsKind(SyntaxKind.FieldKeyword) == true;
        var isOnFieldDeclaration = attributeList.Parent is FieldDeclarationSyntax;
        if (!targetsField || isOnFieldDeclaration)
        {
            return;
        }
        var ownerName = DescribeOwner(attributeList.Parent);
        foreach (var attribute in attributeList.Attributes)
        {
            var attributeConstructor = context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol as IMethodSymbol;
            var isIntercomEventAttribute = SymbolEqualityComparer.Default.Equals(attributeConstructor?.ContainingType, attributeType);
            if (isIntercomEventAttribute)
            {
                context.ReportDiagnostic(Diagnostic.Create(IntercomEventDiagnostics.AttributeOnBackingField, attribute.GetLocation(), ownerName));
            }
        }
    }

    //how the message names the declaration the attribute list belongs to, such as "the property 'Changed'"
    private static string DescribeOwner(SyntaxNode? declaration)
    {
        switch (declaration)
        {
            case PropertyDeclarationSyntax property:
            {
                return $"the property '{property.Identifier.ValueText}'";
            }
            case EventFieldDeclarationSyntax eventField:
            {
                var eventNames = eventField.Declaration.Variables.Select(variable => variable.Identifier.ValueText);
                return $"the event '{string.Join(", ", eventNames)}'";
            }
            case ParameterSyntax parameter:
            {
                return $"the parameter '{parameter.Identifier.ValueText}'";
            }
            default:
            {
                return "this declaration";
            }
        }
    }
}
