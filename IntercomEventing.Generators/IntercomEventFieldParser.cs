using System.Collections.Immutable;
using System.Text;
using IntercomEventing.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntercomEventing.Generators;

/// <summary>
/// Reads one <c>[IntercomEvent]</c> field into the value model the source writer needs, together with the diagnostics that
/// explain why a field cannot be generated. Runs inside the incremental pipeline, so its output holds no symbols or syntax.
/// </summary>
internal static class IntercomEventFieldParser
{
    private const string LibraryNamespaceName = "IntercomEventing";
    private const string EventBaseMetadataName = "IntercomEventBase`1";
    private const string NamePropertyName = "Name";
    private const string HandlerDelegateMetadataName = "System.Func`2";
    private const string TaskMetadataName = "System.Threading.Tasks.Task";
    private const string ViewInterfaceMetadataName = "IntercomEventing.IIntercomEvent`1";
    private const string ObsoleteAttributeName = "System.ObsoleteAttribute";
    private const string ExperimentalAttributeName = "System.Diagnostics.CodeAnalysis.ExperimentalAttribute";

    //the call type exactly as the field declares it, including nullable reference annotations such as EventCall<string?>
    private static readonly SymbolDisplayFormat TypeReferenceFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    //namespace declarations cannot start with global::
    private static readonly SymbolDisplayFormat NamespaceDeclarationFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    //short, readable names for diagnostic messages, such as Outer.Inner or IntercomEvent<UserCreated>
    private static readonly SymbolDisplayFormat MessageFormat = SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    //the message format that also shows the "?" of a nullable reference type, such as IntercomEvent<int>?
    private static readonly SymbolDisplayFormat NullableMessageFormat = MessageFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// The <c>ForAttributeWithMetadataName</c> transform: parses the field that <paramref name="context"/> points at.
    /// </summary>
    public static IntercomEventFieldResult Parse(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IFieldSymbol field)
        {
            return IntercomEventFieldResult.Nothing;
        }
        //a type or attribute argument that does not bind already has a compiler error; generating would only add more errors
        var attribute = context.Attributes[0];
        var nameArgument = FindNamedArgument(attribute, NamePropertyName);
        var nameArgumentIsBroken = nameArgument is { Kind: TypedConstantKind.Error };
        var fieldTypeIsBroken = ContainsErrorType(field.Type) || HasErrorBaseType(field.Type);
        //a second member with the field's name is already CS0102; generating for both fields would add two files with the
        //same hint name, and that failure would drop every file this generator produces in the compilation
        var fieldNameIsDuplicated = field.ContainingType.GetMembers(field.Name).Length > 1;
        //an instance field in a static class is already CS0708 at the field; the generated instance members would repeat it
        var isInstanceFieldOfStaticType = !field.IsStatic && field.ContainingType.IsStatic;
        if (fieldTypeIsBroken || nameArgumentIsBroken || fieldNameIsDuplicated || isInstanceFieldOfStaticType)
        {
            return IntercomEventFieldResult.Nothing;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var fieldLocation = LocationInfo.From(GetFieldIdentifierLocation(context.TargetNode), cancellationToken);
        var diagnostics = new List<DiagnosticInfo>();
        var containingTypes = ReadContainingTypes(field, fieldLocation, diagnostics, cancellationToken);
        if (containingTypes is null)
        {
            //an interface, enum or file-local type can never hold the members, so the other checks would only add noise
            return new IntercomEventFieldResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        var compilation = context.SemanticModel.Compilation;
        var callType = FindCallType(field.Type);
        if (callType is null)
        {
            var fieldTypeName = field.Type.ToDisplayString(MessageFormat);
            var reason = DescribeWhyNotAnEventKind(field.Type);
            diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.FieldTypeNotIntercomEvent, fieldLocation, field.Name, fieldTypeName, reason));
        }
        else if (ContainsErrorType(callType))
        {
            //a custom event kind whose base type argument does not bind; the compiler reports it on that type
            return IntercomEventFieldResult.Nothing;
        }
        else if (ContainsFileLocalType(callType))
        {
            //a file-local payload in a member signature of a type that is not file-local is already CS9051 at the field; the
            //generated file could not even name the payload
            return IntercomEventFieldResult.Nothing;
        }
        else
        {
            CheckFieldNullability(field, fieldLocation, diagnostics);
            CheckCallTypeAccessibility(field, callType, fieldLocation, diagnostics);
            CheckCallTypeIsReachableThroughGlobal(compilation, field, callType, fieldLocation, diagnostics);
        }
        //a const field is not readonly either, but "make it readonly" is the wrong advice for it
        if (field.IsConst)
        {
            diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.ConstField, fieldLocation, field.Name));
        }
        else if (!field.IsReadOnly)
        {
            diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.FieldNotReadOnly, fieldLocation, field.Name));
        }
        //a field that is no event kind (INTEV003) generates nothing, so its name can be neither invalid nor taken; for the
        //same reason FindFieldGeneratingMember does not count such a field when it checks the names of other fields
        if (callType is null)
        {
            return new IntercomEventFieldResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        var nameOverride = nameArgument?.Value as string;
        var eventName = IntercomEventNaming.ResolveEventName(field.Name, nameOverride);
        //a problem with the name is reported where the name came from: the Name argument, or else the field
        var nameLocation = fieldLocation;
        if (nameOverride is not null)
        {
            var nameArgumentLocation = FindNamedArgumentLocation(attribute, NamePropertyName, cancellationToken);
            nameLocation = LocationInfo.From(nameArgumentLocation, cancellationToken) ?? fieldLocation;
        }
        var nameIsValid = CheckEventName(field, eventName, nameOverride is not null, nameLocation, diagnostics);
        if (nameIsValid)
        {
            CheckMemberConflicts(compilation, field, attribute, callType, eventName, nameLocation, diagnostics, cancellationToken);
        }

        var diagnosticArray = new EquatableArray<DiagnosticInfo>(diagnostics.ToArray());
        //warnings (a field that is not readonly) still generate
        var hasErrors = diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
        if (hasErrors)
        {
            return new IntercomEventFieldResult(null, diagnosticArray);
        }
        var model = CreateModel(field, containingTypes, eventName, callType);
        return new IntercomEventFieldResult(model, diagnosticArray);
    }

    /// <summary>
    /// Finds <c>TCall</c> by walking from <paramref name="fieldType"/> through its base types to <c>IntercomEventBase&lt;TCall&gt;</c>.
    /// </summary>
    /// <returns>The call type, or <see langword="null"/> when <paramref name="fieldType"/> is not an IntercomEvent kind.</returns>
    public static ITypeSymbol? FindCallType(ITypeSymbol fieldType)
    {
        //type parameters and interfaces (even IIntercomEvent<TCall>) have no base class, so they are not event kinds
        for (var currentType = fieldType as INamedTypeSymbol; currentType is not null; currentType = currentType.BaseType)
        {
            if (IsIntercomEventBase(currentType.OriginalDefinition))
            {
                return currentType.TypeArguments[0];
            }
        }
        return null;
    }

    //matched by name rather than GetTypeByMetadataName, which returns null when two references define the type
    private static bool IsIntercomEventBase(INamedTypeSymbol typeDefinition)
    {
        var hasEventBaseName = typeDefinition.MetadataName == EventBaseMetadataName;
        var isTopLevelType = typeDefinition.ContainingType is null;
        var containingNamespace = typeDefinition.ContainingNamespace;
        var isInLibraryNamespace = containingNamespace is not null &&
            containingNamespace.Name == LibraryNamespaceName &&
            containingNamespace.ContainingNamespace is { IsGlobalNamespace: true };
        return hasEventBaseName && isTopLevelType && isInLibraryNamespace;
    }

    //returns the containing types outermost first, or null when one of them can never receive generated members
    private static List<ContainingTypeModel>? ReadContainingTypes(IFieldSymbol field, LocationInfo? fieldLocation,
        List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        var containingTypes = new List<ContainingTypeModel>();
        for (var currentType = field.ContainingType; currentType is not null; currentType = currentType.ContainingType)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declarationKeyword = GetDeclarationKeyword(currentType, cancellationToken);
            //a file-local type cannot be extended from the generated file, and neither can a type nested in one
            if (declarationKeyword is null || currentType.IsFileLocal)
            {
                var typeKindDescription = DescribeUnsupportedType(currentType);
                var typeName = currentType.ToDisplayString(MessageFormat);
                diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.UnsupportedContainingType, fieldLocation,
                    field.Name, typeKindDescription, typeName));
                return null;
            }
            if (!IsDeclaredPartial(currentType, cancellationToken))
            {
                var typeName = currentType.ToDisplayString(MessageFormat);
                diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.ContainingTypeNotPartial, fieldLocation, typeName, field.Name));
            }
            var declarationName = CreateDeclarationName(currentType);
            containingTypes.Add(new ContainingTypeModel(declarationKeyword, declarationName));
        }
        containingTypes.Reverse();
        return containingTypes;
    }

    //Inner<U> for "partial struct Inner<U>"; the containing types get their own declarations. Written by hand rather than with
    //ToDisplayString, whose keyword escaping covers reserved keywords only: a type named @required or @scoped would lose its
    //'@' and fail to compile in the generated file, and one named @var would warn there (CS8981)
    private static string CreateDeclarationName(INamedTypeSymbol type)
    {
        var escapedTypeName = IntercomEventNaming.EscapeTypeDeclarationIdentifier(type.Name);
        if (type.TypeParameters.Length == 0)
        {
            return escapedTypeName;
        }
        var escapedTypeParameterNames = type.TypeParameters.Select(static typeParameter => IntercomEventNaming.EscapeTypeDeclarationIdentifier(typeParameter.Name));
        return $"{escapedTypeName}<{string.Join(", ", escapedTypeParameterNames)}>";
    }

    //the keyword the generated partial declaration must repeat (partial parts must all be classes, all records, ...);
    //null for anything the generator cannot extend, including declaration kinds newer than this Roslyn version knows
    private static string? GetDeclarationKeyword(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            return null;
        }
        var declaration = type.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
        switch (declaration)
        {
            case ClassDeclarationSyntax:
            {
                return "class";
            }
            case StructDeclarationSyntax:
            {
                return "struct";
            }
            case RecordDeclarationSyntax recordDeclaration:
            {
                var isRecordStruct = recordDeclaration.IsKind(SyntaxKind.RecordStructDeclaration);
                return isRecordStruct ? "record struct" : "record";
            }
            default:
            {
                return null;
            }
        }
    }

    private static string DescribeUnsupportedType(INamedTypeSymbol type)
    {
        if (type.IsFileLocal)
        {
            return "the file-local type";
        }
        switch (type.TypeKind)
        {
            case TypeKind.Interface:
            {
                return "the interface";
            }
            case TypeKind.Enum:
            {
                return "the enum";
            }
            default:
            {
                return "the type";
            }
        }
    }

    //every declaration must say partial; a type with one non-partial declaration cannot get another part
    private static bool IsDeclaredPartial(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        foreach (var syntaxReference in type.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken) as TypeDeclarationSyntax;
            var hasPartialModifier = declaration is not null && declaration.Modifiers.Any(SyntaxKind.PartialKeyword);
            if (!hasPartialModifier)
            {
                return false;
            }
        }
        return true;
    }

    private static void CheckFieldNullability(IFieldSymbol field, LocationInfo? fieldLocation, List<DiagnosticInfo> diagnostics)
    {
        //only an explicit "?" in a nullable-enabled context; oblivious fields (nullable disabled) are left alone
        if (field.NullableAnnotation != NullableAnnotation.Annotated)
        {
            return;
        }
        var fieldTypeName = field.Type.ToDisplayString(NullableMessageFormat);
        diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.FieldTypeNullable, fieldLocation, field.Name, fieldTypeName));
    }

    private static void CheckCallTypeAccessibility(IFieldSymbol field, ITypeSymbol callType, LocationInfo? fieldLocation,
        List<DiagnosticInfo> diagnostics)
    {
        //the generated members are public, so they are visible wherever the type that declares the field is
        var lessAccessibleType = AccessibilityRules.FindTypeLessAccessibleThanPublicMember(callType, field.ContainingType);
        if (lessAccessibleType is null)
        {
            return;
        }
        var lessAccessibleTypeName = lessAccessibleType.ToDisplayString(MessageFormat);
        var containingTypeName = field.ContainingType.ToDisplayString(MessageFormat);
        diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.CallTypeLessAccessible, fieldLocation,
            field.Name, lessAccessibleTypeName, containingTypeName));
    }

    //the end of the INTEV003 message. A type parameter constrained to an event kind does derive from one, so it gets its own
    //explanation: it is rejected because the generator needs the event's own class type
    private static string DescribeWhyNotAnEventKind(ITypeSymbol fieldType)
    {
        if (fieldType is ITypeParameterSymbol)
        {
            return "which is a type parameter; declare the field with a class type that derives from IntercomEventBase<TCall>, even when the type parameter is constrained to one";
        }
        return "which does not derive from IntercomEventBase<TCall>";
    }

    //the generated file names every type with global::, which cannot reach a type of an assembly referenced only through an
    //extern alias (CS0400 inside the generated file)
    private static void CheckCallTypeIsReachableThroughGlobal(Compilation compilation, IFieldSymbol field, ITypeSymbol callType,
        LocationInfo? fieldLocation, List<DiagnosticInfo> diagnostics)
    {
        foreach (var namedType in EnumerateNamedTypesIn(callType))
        {
            var externAlias = FindExternAliasOnlyReference(compilation, namedType.ContainingAssembly);
            if (externAlias is null)
            {
                continue;
            }
            var aliasedTypeName = namedType.ToDisplayString(MessageFormat);
            var assemblyName = namedType.ContainingAssembly.Name;
            diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.CallTypeFromAliasedReference, fieldLocation,
                field.Name, aliasedTypeName, assemblyName, externAlias));
            return;
        }
    }

    //the first alias of the reference that brings in assembly, when that reference has aliases and global is not one of them;
    //null when the assembly is reachable through global:: (the compilation itself, an unaliased reference, or no direct reference)
    private static string? FindExternAliasOnlyReference(Compilation compilation, IAssemblySymbol? assembly)
    {
        var isOwnOrUnknownAssembly = assembly is null || SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly);
        if (isOwnOrUnknownAssembly)
        {
            return null;
        }
        var reference = compilation.GetMetadataReference(assembly!);
        var aliases = reference?.Properties.Aliases ?? ImmutableArray<string>.Empty;
        var reachableThroughGlobal = aliases.IsDefaultOrEmpty || aliases.Contains("global");
        if (reachableThroughGlobal)
        {
            return null;
        }
        return aliases[0];
    }

    private static bool ContainsFileLocalType(ITypeSymbol type)
    {
        return EnumerateNamedTypesIn(type).Any(static namedType => namedType.IsFileLocal);
    }

    /// <summary>
    /// Every named type that writing <paramref name="type"/> in source mentions: the type, the types it is nested in, and the
    /// type arguments and element types inside them, at any depth.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypesIn(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol arrayType:
            {
                foreach (var elementType in EnumerateNamedTypesIn(arrayType.ElementType))
                {
                    yield return elementType;
                }
                break;
            }
            case IPointerTypeSymbol pointerType:
            {
                foreach (var pointedAtType in EnumerateNamedTypesIn(pointerType.PointedAtType))
                {
                    yield return pointedAtType;
                }
                break;
            }
            case INamedTypeSymbol namedType:
            {
                //Outer<A>.Inner<B> mentions Outer and A as well as Inner and B
                for (var currentType = namedType; currentType is not null; currentType = currentType.ContainingType)
                {
                    yield return currentType;
                    foreach (var typeArgument in currentType.TypeArguments)
                    {
                        foreach (var typeInArgument in EnumerateNamedTypesIn(typeArgument))
                        {
                            yield return typeInArgument;
                        }
                    }
                }
                break;
            }
        }
    }

    //the diagnostic IDs that naming the call type reports because a type in it (or its assembly) is obsolete or experimental.
    //The user already sees them at the field, where they can suppress them, so the generated file disables them instead of
    //repeating them. An obsolete type with error: true is left out: CS0619 cannot be suppressed and already fails the field
    private static string[] CollectCallTypeDiagnosticIdsToSuppress(ITypeSymbol callType)
    {
        var diagnosticIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var namedType in EnumerateNamedTypesIn(callType))
        {
            AddSuppressibleDiagnosticIds(namedType.GetAttributes(), diagnosticIds);
            //[assembly: Experimental] and [module: Experimental] make every type in them experimental
            AddSuppressibleDiagnosticIds(namedType.ContainingAssembly?.GetAttributes() ?? ImmutableArray<AttributeData>.Empty, diagnosticIds);
            AddSuppressibleDiagnosticIds(namedType.ContainingModule?.GetAttributes() ?? ImmutableArray<AttributeData>.Empty, diagnosticIds);
        }
        return diagnosticIds.ToArray();
    }

    private static void AddSuppressibleDiagnosticIds(ImmutableArray<AttributeData> attributes, SortedSet<string> diagnosticIds)
    {
        foreach (var attribute in attributes)
        {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            if (attributeName == ObsoleteAttributeName)
            {
                AddObsoleteDiagnosticIds(attribute, diagnosticIds);
            }
            else if (attributeName == ExperimentalAttributeName)
            {
                var experimentalId = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                AddIfUsableInPragma(experimentalId, diagnosticIds);
            }
        }
    }

    private static void AddObsoleteDiagnosticIds(AttributeData obsoleteAttribute, SortedSet<string> diagnosticIds)
    {
        //ObsoleteAttribute(message, error): error is the second constructor argument
        var constructorArguments = obsoleteAttribute.ConstructorArguments;
        var isError = constructorArguments.Length > 1 && constructorArguments[1].Value is true;
        if (isError)
        {
            return;
        }
        //a DiagnosticId replaces CS0612 and CS0618 with the producer's own ID
        var customDiagnosticId = FindNamedArgument(obsoleteAttribute, "DiagnosticId")?.Value as string;
        if (!string.IsNullOrEmpty(customDiagnosticId))
        {
            AddIfUsableInPragma(customDiagnosticId, diagnosticIds);
            return;
        }
        //CS0612 without a message, CS0618 with one
        diagnosticIds.Add("CS0612");
        diagnosticIds.Add("CS0618");
    }

    //a #pragma warning list accepts identifiers only; an ID with other characters would break the generated file
    private static void AddIfUsableInPragma(string? diagnosticId, SortedSet<string> diagnosticIds)
    {
        var isUsable = !string.IsNullOrEmpty(diagnosticId) && diagnosticId!.All(static character => char.IsLetterOrDigit(character) || character == '_');
        if (isUsable)
        {
            diagnosticIds.Add(diagnosticId!);
        }
    }

    //[Obsolete] and [Experimental] on the field, written so the generated members repeat them: their users then get the
    //diagnostic the field's author asked for, and the members' own use of the field is inside the same context, so it reports
    //nothing in the generated file
    private static string[] CollectAttributesToForward(IFieldSymbol field)
    {
        var forwardedAttributes = new List<string>();
        foreach (var attribute in field.GetAttributes())
        {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            var isForwarded = attributeName is ObsoleteAttributeName or ExperimentalAttributeName;
            if (!isForwarded)
            {
                continue;
            }
            var attributeClassName = attribute.AttributeClass!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var positionalArguments = attribute.ConstructorArguments.Select(static argument => argument.ToCSharpString());
            var namedArguments = attribute.NamedArguments.Select(static argument => $"{argument.Key} = {argument.Value.ToCSharpString()}");
            var arguments = string.Join(", ", positionalArguments.Concat(namedArguments));
            forwardedAttributes.Add($"{attributeClassName}({arguments})");
        }
        return forwardedAttributes.ToArray();
    }

    private static bool CheckEventName(IFieldSymbol field, string eventName, bool nameIsOverridden, LocationInfo? nameLocation,
        List<DiagnosticInfo> diagnostics)
    {
        if (IntercomEventNaming.IsValidMemberName(eventName))
        {
            return true;
        }
        var advice = nameIsOverridden
            ? "set IntercomEventAttribute.Name to a C# identifier that is not a keyword"
            : "it is derived from the field name, so set IntercomEventAttribute.Name to choose a valid name";
        diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.InvalidEventName, nameLocation, eventName, field.Name, advice));
        return false;
    }

    private static void CheckMemberConflicts(Compilation compilation, IFieldSymbol field, AttributeData attribute, ITypeSymbol callType,
        string eventName, LocationInfo? nameLocation, List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        var attributeClass = attribute.AttributeClass;
        string[] generatedMemberNames = [eventName, IntercomEventNaming.GetEventPropertyName(eventName)];
        foreach (var generatedMemberName in generatedMemberNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingConflict = DescribeExistingConflict(compilation, field, attributeClass, generatedMemberName)
                ?? DescribeSiblingAccessorNamed(field, attributeClass, generatedMemberName);
            if (existingConflict is not null)
            {
                diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.GeneratedMemberConflict, nameLocation,
                    generatedMemberName, field.Name, existingConflict));
            }
            var conflictingSibling = FindSiblingGeneratingMember(field, attributeClass, generatedMemberName);
            if (conflictingSibling is not null)
            {
                diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.DuplicateGeneratedMember, nameLocation,
                    field.Name, conflictingSibling.Name, generatedMemberName));
            }
        }

        var accessorConflict = DescribeReservedAccessorConflict(compilation, field, attributeClass, callType, eventName);
        if (accessorConflict is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(IntercomEventDiagnostics.GeneratedMemberConflict, nameLocation,
                accessorConflict.Value.GeneratedMemberName, field.Name, accessorConflict.Value.Conflict));
        }
    }

    //describes what generatedMemberName would clash with or hide, for the diagnostic message; null when the name is free
    private static string? DescribeExistingConflict(Compilation compilation, IFieldSymbol field, INamedTypeSymbol? attributeClass,
        string generatedMemberName)
    {
        var containingType = field.ContainingType;
        if (containingType.Name == generatedMemberName)
        {
            return $"its containing type '{containingType.ToDisplayString(MessageFormat)}'";
        }
        var clashesWithTypeParameter = containingType.TypeParameters.Any(typeParameter => typeParameter.Name == generatedMemberName);
        if (clashesWithTypeParameter)
        {
            return $"the type parameter '{generatedMemberName}' of '{containingType.ToDisplayString(MessageFormat)}'";
        }
        var declaredMember = containingType.GetMembers(generatedMemberName).FirstOrDefault();
        if (declaredMember is not null)
        {
            return $"the member '{declaredMember.ToDisplayString(MessageFormat)}'";
        }
        //an indexer is named this[] for lookup, but it occupies its metadata name (Item, or its [IndexerName]) in the type
        var conflictingIndexer = containingType.GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(property => property.IsIndexer && property.MetadataName == generatedMemberName);
        if (conflictingIndexer is not null)
        {
            return $"the indexer '{conflictingIndexer.ToDisplayString(MessageFormat)}', whose metadata name is '{generatedMemberName}'";
        }

        //a generated member with the name of an inherited one would hide it (CS0108 inside the generated file)
        for (var baseType = containingType.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            var inheritedMember = baseType.GetMembers(generatedMemberName)
                .FirstOrDefault(member => IsHiddenByNonGenericMember(member) && compilation.IsSymbolAccessibleWithin(member, containingType));
            if (inheritedMember is not null)
            {
                return $"the inherited member '{inheritedMember.ToDisplayString(MessageFormat)}'";
            }
            //members this generator adds to a base type declared in this compilation are not in the compilation it reads;
            //a base type from a referenced assembly already contains its generated members, which the lookup above found
            var baseTypeIsInSource = baseType.DeclaringSyntaxReferences.Length > 0;
            if (!baseTypeIsInSource)
            {
                continue;
            }
            var baseFieldGeneratingMember = FindFieldGeneratingMember(baseType, attributeClass, generatedMemberName, fieldToSkip: null);
            if (baseFieldGeneratingMember is not null)
            {
                var baseTypeName = baseType.ToDisplayString(MessageFormat);
                return $"the inherited member '{baseTypeName}.{generatedMemberName}' generated for field '{baseFieldGeneratingMember.Name}'";
            }
        }
        return null;
    }

    //the compiler hides only members of the same name and arity: a base method Changed<T>() or type ChangedEvent<T> stays
    //visible beside a generated Changed event or ChangedEvent property, without CS0108
    private static bool IsHiddenByNonGenericMember(ISymbol inheritedMember)
    {
        var isGenericMethod = inheritedMember is IMethodSymbol { IsGenericMethod: true };
        var isGenericType = inheritedMember is INamedTypeSymbol { Arity: > 0 };
        return !isGenericMethod && !isGenericType;
    }

    //the add and remove accessors reserve add_<Event> and remove_<Event> with one handler parameter, and the get-only view
    //property reserves get_<Event>Event with none and set_<Event>Event with one view parameter. A method with that name and
    //those parameters is CS0082 inside the generated file, and any other member with that name, including a member generated
    //for another [IntercomEvent] field, is CS0102
    private static (string GeneratedMemberName, string Conflict)? DescribeReservedAccessorConflict(Compilation compilation,
        IFieldSymbol field, INamedTypeSymbol? attributeClass, ITypeSymbol callType, string eventName)
    {
        var handlerType = ConstructCallTypeGeneric(compilation, HandlerDelegateMetadataName, callType, TaskMetadataName);
        var viewType = ConstructCallTypeGeneric(compilation, ViewInterfaceMetadataName, callType, secondTypeArgumentMetadataName: null);
        foreach (var accessor in DescribeReservedAccessors(eventName, handlerType, viewType))
        {
            var declaredConflict = DescribeDeclaredMemberTakingAccessorName(compilation, field.ContainingType, accessor);
            if (declaredConflict is not null)
            {
                return (accessor.MemberName, declaredConflict);
            }
            var siblingField = FindSiblingGeneratingMember(field, attributeClass, accessor.AccessorName);
            if (siblingField is not null)
            {
                var siblingConflict = $"the member '{accessor.AccessorName}' generated for field '{siblingField.Name}', which has the name of its {accessor.Kind} accessor";
                return (accessor.MemberName, siblingConflict);
            }
        }
        return null;
    }

    //the accessors of the members generated for an event named eventName. A parameter type that could not be built (the
    //framework or library type does not resolve) is null, and then only the accessor's name is checked
    private static ReservedAccessor[] DescribeReservedAccessors(string eventName, ITypeSymbol? handlerType, ITypeSymbol? viewType)
    {
        var eventPropertyName = IntercomEventNaming.GetEventPropertyName(eventName);
        return
        [
            new ReservedAccessor("add", eventName, ParameterCount: 1, handlerType),
            new ReservedAccessor("remove", eventName, ParameterCount: 1, handlerType),
            new ReservedAccessor("get", eventPropertyName, ParameterCount: 0, ParameterType: null),
            new ReservedAccessor("set", eventPropertyName, ParameterCount: 1, viewType),
        ];
    }

    //Func<TCall, Task> or IIntercomEvent<TCall>; null when the generic type or its second argument does not resolve (a
    //compilation that lacks them, or references two copies of them)
    private static INamedTypeSymbol? ConstructCallTypeGeneric(Compilation compilation, string genericTypeMetadataName, ITypeSymbol callType,
        string? secondTypeArgumentMetadataName)
    {
        var genericType = compilation.GetTypeByMetadataName(genericTypeMetadataName);
        if (genericType is null)
        {
            return null;
        }
        if (secondTypeArgumentMetadataName is null)
        {
            return genericType.Construct(callType);
        }
        var secondTypeArgument = compilation.GetTypeByMetadataName(secondTypeArgumentMetadataName);
        if (secondTypeArgument is null)
        {
            return null;
        }
        return genericType.Construct(callType, secondTypeArgument);
    }

    //a declared method with the accessor's name and parameters, or any other declared member with the accessor's name;
    //null when the name is free. Other ordinary methods with that name are legal overloads, and non-ordinary methods (the
    //accessors of a declared property or event) are already conflicts of that property or event itself
    private static string? DescribeDeclaredMemberTakingAccessorName(Compilation compilation, INamedTypeSymbol containingType,
        ReservedAccessor accessor)
    {
        foreach (var member in containingType.GetMembers(accessor.AccessorName))
        {
            var memberName = member.ToDisplayString(MessageFormat);
            if (member is not IMethodSymbol method)
            {
                return $"the member '{memberName}', which has the name of its {accessor.Kind} accessor";
            }
            var isOrdinaryMethod = method.MethodKind == MethodKind.Ordinary;
            if (isOrdinaryMethod && HasAccessorParameters(compilation, method, accessor))
            {
                return $"the method '{memberName}', which has the name and parameters of its {accessor.Kind} accessor";
            }
        }
        return null;
    }

    //the reservation compares parameters only, so a generic method with the accessor's parameters clashes too. An identity
    //conversion ignores exactly what a signature ignores: nullability, tuple element names, and dynamic against object
    private static bool HasAccessorParameters(Compilation compilation, IMethodSymbol method, ReservedAccessor accessor)
    {
        if (method.Parameters.Length != accessor.ParameterCount)
        {
            return false;
        }
        if (accessor.ParameterCount == 0)
        {
            return true;
        }
        if (accessor.ParameterType is null)
        {
            return false;
        }
        var parameter = method.Parameters[0];
        var isByValue = parameter.RefKind == RefKind.None;
        var hasAccessorParameterType = compilation.ClassifyCommonConversion(parameter.Type, accessor.ParameterType).IsIdentity;
        return isByValue && hasAccessorParameterType;
    }

    //describes the accessor of another [IntercomEvent] field's generated member that generatedMemberName would take, such as
    //get_ChangedEvent of the ChangedEvent property generated for _changed; null when no such accessor exists
    private static string? DescribeSiblingAccessorNamed(IFieldSymbol field, INamedTypeSymbol? attributeClass, string generatedMemberName)
    {
        foreach (var sibling in EnumerateGeneratingFields(field.ContainingType, attributeClass, fieldToSkip: field))
        {
            foreach (var accessor in DescribeReservedAccessors(sibling.EventName, handlerType: null, viewType: null))
            {
                if (accessor.AccessorName == generatedMemberName)
                {
                    return $"the {accessor.Kind} accessor of the member '{accessor.MemberName}' generated for field '{sibling.Field.Name}'";
                }
            }
        }
        return null;
    }

    private static IFieldSymbol? FindSiblingGeneratingMember(IFieldSymbol field, INamedTypeSymbol? attributeClass, string generatedMemberName)
    {
        return FindFieldGeneratingMember(field.ContainingType, attributeClass, generatedMemberName, fieldToSkip: field);
    }

    //finds another [IntercomEvent] field of type whose generated event or Event property is named generatedMemberName
    private static IFieldSymbol? FindFieldGeneratingMember(INamedTypeSymbol type, INamedTypeSymbol? attributeClass,
        string generatedMemberName, IFieldSymbol? fieldToSkip)
    {
        foreach (var candidate in EnumerateGeneratingFields(type, attributeClass, fieldToSkip))
        {
            var generatesEvent = candidate.EventName == generatedMemberName;
            var generatesEventProperty = IntercomEventNaming.GetEventPropertyName(candidate.EventName) == generatedMemberName;
            if (generatesEvent || generatesEventProperty)
            {
                return candidate.Field;
            }
        }
        return null;
    }

    //every [IntercomEvent] field of type that can generate members, with the event name it generates
    private static IEnumerable<(IFieldSymbol Field, string EventName)> EnumerateGeneratingFields(INamedTypeSymbol type,
        INamedTypeSymbol? attributeClass, IFieldSymbol? fieldToSkip)
    {
        if (attributeClass is null)
        {
            yield break;
        }
        foreach (var candidateField in type.GetMembers().OfType<IFieldSymbol>())
        {
            var isFieldToSkip = SymbolEqualityComparer.Default.Equals(candidateField.OriginalDefinition, fieldToSkip?.OriginalDefinition);
            if (isFieldToSkip)
            {
                continue;
            }
            var candidateAttribute = candidateField.GetAttributes()
                .FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeClass));
            if (candidateAttribute is null)
            {
                continue;
            }
            //a field whose type is no event kind (INTEV003) never generates members, so it cannot take a name
            var candidateIsEventKind = FindCallType(candidateField.Type) is not null;
            if (!candidateIsEventKind)
            {
                continue;
            }
            var candidateNameOverride = FindNamedArgument(candidateAttribute, NamePropertyName)?.Value as string;
            var candidateEventName = IntercomEventNaming.ResolveEventName(candidateField.Name, candidateNameOverride);
            yield return (candidateField, candidateEventName);
        }
    }

    private static IntercomEventFieldModel CreateModel(IFieldSymbol field, List<ContainingTypeModel> containingTypes, string eventName,
        ITypeSymbol callType)
    {
        var containingType = field.ContainingType;
        var containingNamespace = containingType.ContainingNamespace;
        string? namespaceName = null;
        if (containingNamespace is not null && !containingNamespace.IsGlobalNamespace)
        {
            namespaceName = containingNamespace.ToDisplayString(NamespaceDeclarationFormat);
        }

        //always qualified: a field named value would otherwise bind to the accessor's value parameter, and one named field to
        //the field keyword inside the property accessor
        var escapedFieldName = IntercomEventNaming.EscapeIdentifier(field.Name);
        var fieldOwner = field.IsStatic ? containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : "this";
        var fieldAccessExpression = $"{fieldOwner}.{escapedFieldName}";

        var callTypeName = callType.ToDisplayString(TypeReferenceFormat);
        var exampleReceiver = field.IsStatic ? containingType.ToDisplayString(MessageFormat) : CreateExampleVariableName(containingType.Name);
        var hintName = CreateHintName(field);
        var forwardedAttributes = new EquatableArray<string>(CollectAttributesToForward(field));
        var suppressedDiagnosticIds = new EquatableArray<string>(CollectCallTypeDiagnosticIdsToSuppress(callType));
        return new IntercomEventFieldModel(hintName, namespaceName, new EquatableArray<ContainingTypeModel>(containingTypes.ToArray()),
            eventName, callTypeName, fieldAccessExpression, field.IsStatic, exampleReceiver, forwardedAttributes, suppressedDiagnosticIds);
    }

    //UserRegistry becomes userRegistry; a result that is not a usable identifier falls back to producer
    private static string CreateExampleVariableName(string typeName)
    {
        if (typeName.Length == 0)
        {
            return "producer";
        }
        var variableName = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
        if (!IntercomEventNaming.IsValidMemberName(variableName))
        {
            return "producer";
        }
        return variableName;
    }

    //Namespace.Outer`1.Inner._field.<hash>.g.cs. Hint names must be unique ignoring case, but C# names are case-sensitive
    //(fields _a and _A, types Box and box), so a hash of the exact, case-sensitive identity keeps them apart
    private static string CreateHintName(IFieldSymbol field)
    {
        //Name and MetadataName are unescaped (class for @class); ToDisplayString would add the '@', which AddSource rejects
        var typeChain = new List<string>();
        for (var currentType = field.ContainingType; currentType is not null; currentType = currentType.ContainingType)
        {
            typeChain.Add(currentType.MetadataName);
        }
        typeChain.Reverse();
        var namespaceParts = new List<string>();
        var currentNamespace = field.ContainingType.ContainingNamespace;
        while (currentNamespace is { IsGlobalNamespace: false })
        {
            namespaceParts.Add(currentNamespace.Name);
            currentNamespace = currentNamespace.ContainingNamespace;
        }
        namespaceParts.Reverse();

        //the readable part joins every name with '.'
        string[] readableParts = [.. namespaceParts, .. typeChain, field.Name];
        var readableIdentity = string.Join(".", readableParts);
        //the hashed identity separates nested types with '+', as metadata names do. Type C nested in type A.B and type C in
        //namespace A.B read the same, and a program that declares both (CS0101) must still not get two equal hint names:
        //AddSource would throw, and that drops every file this generator adds to the compilation
        var nestedTypeIdentity = string.Join("+", typeChain);
        string[] exactParts = [.. namespaceParts, nestedTypeIdentity, field.Name];
        var exactIdentity = string.Join(".", exactParts);
        return $"{ToHintNameCharacters(readableIdentity)}.{ComputeStableHash(exactIdentity):X8}.g.cs";
    }

    //AddSource accepts letters, digits and a few punctuation characters; an identifier can also hold combining marks,
    //connector punctuation and formatting characters. Each of those becomes '_', and the hash of the unchanged identity
    //keeps names that become equal apart
    private static string ToHintNameCharacters(string identity)
    {
        var hintName = new StringBuilder(identity.Length);
        foreach (var character in identity)
        {
            var isAlwaysAccepted = char.IsLetterOrDigit(character) || character is '.' or '_' or '`';
            hintName.Append(isAlwaysAccepted ? character : '_');
        }
        return hintName.ToString();
    }

    //FNV-1a: string.GetHashCode is randomized per process, and hint names must not change between builds
    private static uint ComputeStableHash(string text)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;
        unchecked
        {
            var hash = OffsetBasis;
            foreach (var character in text)
            {
                hash ^= character;
                hash *= Prime;
            }
            return hash;
        }
    }

    private static TypedConstant? FindNamedArgument(AttributeData attribute, string argumentName)
    {
        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == argumentName)
            {
                return namedArgument.Value;
            }
        }
        return null;
    }

    private static Location? FindNamedArgumentLocation(AttributeData attribute, string argumentName, CancellationToken cancellationToken)
    {
        var attributeSyntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        if (attributeSyntax?.ArgumentList is null)
        {
            return null;
        }
        foreach (var argument in attributeSyntax.ArgumentList.Arguments)
        {
            if (argument.NameEquals?.Name.Identifier.ValueText == argumentName)
            {
                return argument.Expression.GetLocation();
            }
        }
        return null;
    }

    private static Location GetFieldIdentifierLocation(SyntaxNode targetNode)
    {
        switch (targetNode)
        {
            case VariableDeclaratorSyntax variableDeclarator:
            {
                return variableDeclarator.Identifier.GetLocation();
            }
            case EnumMemberDeclarationSyntax enumMember:
            {
                return enumMember.Identifier.GetLocation();
            }
            default:
            {
                return targetNode.GetLocation();
            }
        }
    }

    //a custom event kind deriving from a type that does not bind cannot be classified; the compiler reports the base type
    private static bool HasErrorBaseType(ITypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.TypeKind == TypeKind.Error)
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsErrorType(ITypeSymbol type)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
            {
                return true;
            }
            case IArrayTypeSymbol arrayType:
            {
                return ContainsErrorType(arrayType.ElementType);
            }
            case INamedTypeSymbol namedType:
            {
                return namedType.TypeArguments.Any(ContainsErrorType);
            }
            default:
            {
                return false;
            }
        }
    }

    //one accessor name the generated members reserve, such as get_UserCreatedEvent, with the parameters it is reserved for;
    //ParameterType is null when the accessor has none or its type could not be built
    private sealed record ReservedAccessor(string Kind, string MemberName, int ParameterCount, ITypeSymbol? ParameterType)
    {
        public string AccessorName => $"{Kind}_{MemberName}";
    }
}
