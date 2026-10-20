using Microsoft.CodeAnalysis;

namespace IntercomEventing.Generators;

/// <summary>
/// The C# accessibility-consistency rule the generated public members must satisfy: every type in a member's signature
/// must be accessible everywhere the member is (otherwise CS0053 / CS7025 inside the generated file).
/// </summary>
/// <remarks>
/// This mirrors the compiler's own check (<c>IsAsRestrictive</c> in Roslyn's <c>TypeSymbolExtensions</c>): for each type
/// in the signature, at least one level of the member's containing-type chain must be at least as restrictive as that
/// type's declared accessibility. A private type is only visible inside its containing type, so a member exposing it must
/// be private to that same type (or a type nested in it); a protected type needs a level that is private or protected in a
/// type deriving from the type that declares it; an internal type needs a level restricted to an assembly that can see it.
/// </remarks>
internal static class AccessibilityRules
{
    /// <summary>
    /// Finds the first type in <paramref name="type"/> (the type itself, its containing types and its type arguments, at
    /// any depth) that a public member declared in <paramref name="memberContainingType"/> cannot expose;
    /// <see langword="null"/> when there is none.
    /// </summary>
    public static ITypeSymbol? FindTypeLessAccessibleThanPublicMember(ITypeSymbol type, INamedTypeSymbol memberContainingType)
    {
        switch (type)
        {
            case IArrayTypeSymbol arrayType:
            {
                return FindTypeLessAccessibleThanPublicMember(arrayType.ElementType, memberContainingType);
            }
            case IPointerTypeSymbol pointerType:
            {
                return FindTypeLessAccessibleThanPublicMember(pointerType.PointedAtType, memberContainingType);
            }
            case INamedTypeSymbol namedType:
            {
                return FindNamedTypeLessAccessibleThanPublicMember(namedType, memberContainingType);
            }
            default:
            {
                //type parameters belong to the containing type, and dynamic or function pointers restrict nothing
                return null;
            }
        }
    }

    private static ITypeSymbol? FindNamedTypeLessAccessibleThanPublicMember(INamedTypeSymbol namedType, INamedTypeSymbol memberContainingType)
    {
        //Outer.Inner is only as visible as Outer, and Outer<Internal>.Inner carries Internal through its containing type,
        //so every level is checked together with its own type arguments
        for (var typeLevel = namedType; typeLevel is not null; typeLevel = typeLevel.ContainingType)
        {
            if (!IsMemberAsRestrictiveAsTypeLevel(typeLevel, memberContainingType))
            {
                return namedType;
            }
            foreach (var typeArgument in typeLevel.TypeArguments)
            {
                var lessAccessibleArgument = FindTypeLessAccessibleThanPublicMember(typeArgument, memberContainingType);
                if (lessAccessibleArgument is not null)
                {
                    return lessAccessibleArgument;
                }
            }
        }
        return null;
    }

    //whether a public member of memberContainingType is visible only where typeLevel's own declared accessibility allows.
    //The member itself is public, so only memberContainingType and the types around it can restrict it
    private static bool IsMemberAsRestrictiveAsTypeLevel(INamedTypeSymbol typeLevel, INamedTypeSymbol memberContainingType)
    {
        var typeAccessibility = typeLevel.DeclaredAccessibility;
        var typeRestrictsNothing = typeAccessibility is Accessibility.Public or Accessibility.NotApplicable;
        if (typeRestrictsNothing)
        {
            return true;
        }
        for (var memberLevel = memberContainingType; memberLevel is not null; memberLevel = memberLevel.ContainingType)
        {
            if (MemberLevelRestrictsEnough(typeLevel, memberLevel))
            {
                return true;
            }
        }
        return false;
    }

    //one memberLevel (memberContainingType or a type containing it) narrows the member's visibility at least as much as
    //typeLevel's declared accessibility narrows the type's
    private static bool MemberLevelRestrictsEnough(INamedTypeSymbol typeLevel, INamedTypeSymbol memberLevel)
    {
        var typeAccessibility = typeLevel.DeclaredAccessibility;
        var memberAccessibility = memberLevel.DeclaredAccessibility;
        switch (typeAccessibility)
        {
            case Accessibility.Internal:
            {
                return IsRestrictedToAssemblyThatSeesType(typeLevel, memberLevel);
            }
            case Accessibility.ProtectedAndInternal:
            {
                //private protected: the same level must satisfy both the internal and the protected rule
                return IsRestrictedToAssemblyThatSeesType(typeLevel, memberLevel) && IsRestrictedToDerivedTypes(typeLevel, memberLevel);
            }
            case Accessibility.Protected:
            {
                return IsRestrictedToDerivedTypes(typeLevel, memberLevel);
            }
            case Accessibility.ProtectedOrInternal:
            {
                return IsRestrictedForProtectedInternalType(typeLevel, memberLevel);
            }
            case Accessibility.Private:
            {
                //a private type is visible inside the type that declares it, so the member must be private to that type
                //or to a type nested in it
                var typeDeclaringType = typeLevel.ContainingType;
                var memberIsPrivate = memberAccessibility == Accessibility.Private;
                return memberIsPrivate && typeDeclaringType is not null && IsWithin(memberLevel.ContainingType, typeDeclaringType);
            }
            default:
            {
                return false;
            }
        }
    }

    //the internal rule: memberLevel is private, internal or private protected, in an assembly that can see typeLevel's internals
    private static bool IsRestrictedToAssemblyThatSeesType(INamedTypeSymbol typeLevel, INamedTypeSymbol memberLevel)
    {
        var memberIsAssemblyRestricted = memberLevel.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal
            or Accessibility.ProtectedAndInternal;
        return memberIsAssemblyRestricted && HasInternalAccess(memberLevel, typeLevel);
    }

    //the protected rule: memberLevel is private inside, or protected in, a type deriving from the type that declares typeLevel
    private static bool IsRestrictedToDerivedTypes(INamedTypeSymbol typeLevel, INamedTypeSymbol memberLevel)
    {
        var typeDeclaringType = typeLevel.ContainingType;
        if (typeDeclaringType is null)
        {
            return false;
        }
        switch (memberLevel.DeclaredAccessibility)
        {
            case Accessibility.Private:
            {
                for (var memberParent = memberLevel.ContainingType; memberParent is not null; memberParent = memberParent.ContainingType)
                {
                    if (DerivesFromOrIs(memberParent, typeDeclaringType))
                    {
                        return true;
                    }
                }
                return false;
            }
            case Accessibility.Protected:
            case Accessibility.ProtectedAndInternal:
            {
                var memberParent = memberLevel.ContainingType;
                return memberParent is not null && DerivesFromOrIs(memberParent, typeDeclaringType);
            }
            default:
            {
                return false;
            }
        }
    }

    //protected internal is visible in the assembly and in derived types, so either restriction is enough, except that a
    //protected internal member level needs both
    private static bool IsRestrictedForProtectedInternalType(INamedTypeSymbol typeLevel, INamedTypeSymbol memberLevel)
    {
        var typeDeclaringType = typeLevel.ContainingType;
        var memberParent = memberLevel.ContainingType;
        if (typeDeclaringType is null)
        {
            return false;
        }
        var hasInternalAccess = HasInternalAccess(memberLevel, typeLevel);
        var memberParentDerivesFromTypeParent = memberParent is not null && DerivesFromOrIs(memberParent, typeDeclaringType);
        switch (memberLevel.DeclaredAccessibility)
        {
            case Accessibility.Private:
            {
                if (hasInternalAccess)
                {
                    return true;
                }
                for (var currentParent = memberParent; currentParent is not null; currentParent = currentParent.ContainingType)
                {
                    if (DerivesFromOrIs(currentParent, typeDeclaringType))
                    {
                        return true;
                    }
                }
                return false;
            }
            case Accessibility.Internal:
            {
                return hasInternalAccess;
            }
            case Accessibility.Protected:
            {
                return memberParentDerivesFromTypeParent;
            }
            case Accessibility.ProtectedAndInternal:
            {
                return hasInternalAccess || memberParentDerivesFromTypeParent;
            }
            case Accessibility.ProtectedOrInternal:
            {
                return hasInternalAccess && memberParentDerivesFromTypeParent;
            }
            default:
            {
                return false;
            }
        }
    }

    //the member's assembly is typeLevel's own assembly or one it grants InternalsVisibleTo
    private static bool HasInternalAccess(INamedTypeSymbol memberLevel, INamedTypeSymbol typeLevel)
    {
        var memberAssembly = memberLevel.ContainingAssembly;
        var typeAssembly = typeLevel.ContainingAssembly;
        if (memberAssembly is null || typeAssembly is null)
        {
            return false;
        }
        return SymbolEqualityComparer.Default.Equals(memberAssembly, typeAssembly) || typeAssembly.GivesAccessTo(memberAssembly);
    }

    //whether type is outerType or nested (at any depth) inside it
    private static bool IsWithin(INamedTypeSymbol? type, INamedTypeSymbol outerType)
    {
        for (var currentType = type; currentType is not null; currentType = currentType.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(currentType.OriginalDefinition, outerType.OriginalDefinition))
            {
                return true;
            }
        }
        return false;
    }

    //whether derivedType is baseType or inherits from it (through base classes, or interfaces when baseType is one)
    private static bool DerivesFromOrIs(INamedTypeSymbol derivedType, INamedTypeSymbol baseType)
    {
        var baseDefinition = baseType.OriginalDefinition;
        for (var currentType = derivedType; currentType is not null; currentType = currentType.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(currentType.OriginalDefinition, baseDefinition))
            {
                return true;
            }
        }
        if (baseDefinition.TypeKind != TypeKind.Interface)
        {
            return false;
        }
        return derivedType.AllInterfaces.Any(implementedInterface =>
            SymbolEqualityComparer.Default.Equals(implementedInterface.OriginalDefinition, baseDefinition));
    }
}
