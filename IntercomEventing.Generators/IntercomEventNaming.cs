using Microsoft.CodeAnalysis.CSharp;

namespace IntercomEventing.Generators;

/// <summary>
/// Turns an <c>[IntercomEvent]</c> field into the names of its generated members.
/// </summary>
internal static class IntercomEventNaming
{
    private const string EventPropertySuffix = "Event";

    /// <summary>
    /// The event name: <paramref name="nameOverride"/> when set, else the name derived from <paramref name="fieldName"/>.
    /// </summary>
    public static string ResolveEventName(string fieldName, string? nameOverride)
    {
        if (nameOverride is not null)
        {
            return nameOverride;
        }
        return DeriveEventName(fieldName);
    }

    /// <summary>
    /// The field name without its prefix, with its first letter upper-cased and without a trailing <c>Event</c>:
    /// <c>_userCreated</c>, <c>m_userCreated</c>, <c>userCreated</c> and <c>_userCreatedEvent</c> all become <c>UserCreated</c>.
    /// </summary>
    /// <remarks>
    /// The <c>Event</c> suffix stays when removing it would leave no valid member name (<c>_event</c> becomes <c>Event</c>,
    /// <c>_1Event</c> stays <c>1Event</c>). Returns an empty string when nothing is left after the prefix, such as for <c>_</c>.
    /// </remarks>
    public static string DeriveEventName(string fieldName)
    {
        var nameWithoutPrefix = RemoveFieldPrefix(fieldName);
        if (nameWithoutPrefix.Length == 0)
        {
            return nameWithoutPrefix;
        }
        var capitalizedName = char.ToUpperInvariant(nameWithoutPrefix[0]) + nameWithoutPrefix.Substring(1);
        return RemoveEventSuffix(capitalizedName);
    }

    /// <summary>
    /// The name of the generated view property: the event name plus <c>Event</c>.
    /// </summary>
    public static string GetEventPropertyName(string eventName)
    {
        return eventName + EventPropertySuffix;
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be used as a member name without escaping: an identifier that is not a reserved keyword.
    /// </summary>
    /// <remarks>
    /// Contextual keywords such as <c>value</c> are accepted, because they are valid member names.
    /// </remarks>
    public static bool IsValidMemberName(string name)
    {
        var isIdentifier = SyntaxFacts.IsValidIdentifier(name);
        var isReservedKeyword = SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None;
        return isIdentifier && !isReservedKeyword;
    }

    /// <summary>
    /// <paramref name="identifier"/> as it must be written in source: reserved keywords get an <c>@</c> prefix.
    /// </summary>
    public static string EscapeIdentifier(string identifier)
    {
        var isReservedKeyword = SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None;
        if (isReservedKeyword)
        {
            return "@" + identifier;
        }
        return identifier;
    }

    /// <summary>
    /// A type or type parameter name as a declaration must write it: with an <c>@</c> prefix when it is a reserved keyword or
    /// consists of lowercase ASCII letters only.
    /// </summary>
    /// <remarks>
    /// Every contextual keyword is lowercase ASCII, so the second rule covers the ones that cannot name a type without the
    /// <c>@</c> (<c>required</c>, <c>scoped</c>, <c>file</c>, <c>record</c>, and newer ones such as <c>extension</c>, which an older
    /// Roslyn does not know) and the names that would warn with CS8981 (<c>var</c>, <c>field</c>). The <c>@</c> is legal before
    /// any identifier and does not change the name, so the generated part still matches the user's declaration.
    /// </remarks>
    public static string EscapeTypeDeclarationIdentifier(string identifier)
    {
        var isReservedKeyword = SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None;
        var isLowercaseAsciiOnly = identifier.Length > 0 && identifier.All(static character => character is >= 'a' and <= 'z');
        if (isReservedKeyword || isLowercaseAsciiOnly)
        {
            return "@" + identifier;
        }
        return identifier;
    }

    private static string RemoveFieldPrefix(string fieldName)
    {
        //m_ and s_ are the common member and static prefixes; otherwise every leading underscore goes (_name and __name)
        var hasMemberPrefix = fieldName.StartsWith("m_", StringComparison.Ordinal);
        var hasStaticPrefix = fieldName.StartsWith("s_", StringComparison.Ordinal);
        if (hasMemberPrefix || hasStaticPrefix)
        {
            return fieldName.Substring(2);
        }
        return fieldName.TrimStart('_');
    }

    //the view property appends Event, so a field whose name already ends in it (_userJoinedEvent) would otherwise generate
    //UserJoinedEvent and UserJoinedEventEvent. The suffix is matched case-sensitively and removed at most once
    private static string RemoveEventSuffix(string eventName)
    {
        var hasEventSuffix = eventName.EndsWith(EventPropertySuffix, StringComparison.Ordinal);
        if (!hasEventSuffix)
        {
            return eventName;
        }
        var nameWithoutSuffix = eventName.Substring(0, eventName.Length - EventPropertySuffix.Length);
        //_event leaves nothing and _1Event leaves 1; both keep the suffix, so the name stays what the field says
        var remainderIsUsableName = nameWithoutSuffix.Length > 0 && IsValidMemberName(nameWithoutSuffix);
        if (!remainderIsUsableName)
        {
            return eventName;
        }
        return nameWithoutSuffix;
    }
}
