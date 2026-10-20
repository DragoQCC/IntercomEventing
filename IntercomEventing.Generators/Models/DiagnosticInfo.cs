using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IntercomEventing.Generators.Models;

/// <summary>
/// A diagnostic as plain values, so it can travel through the incremental pipeline and compare equal across runs.
/// A <see cref="Diagnostic"/> or <see cref="Microsoft.CodeAnalysis.Location"/> holds its syntax tree, which would keep old
/// trees alive and defeat caching.
/// </summary>
/// <param name="Descriptor">The rule being reported.</param>
/// <param name="Location">Where to report it; <see langword="null"/> reports it without a location.</param>
/// <param name="MessageArguments">Values for the placeholders in the descriptor's message format.</param>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string> MessageArguments)
{
    /// <summary>
    /// Creates the information for one diagnostic.
    /// </summary>
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] messageArguments)
    {
        return new DiagnosticInfo(descriptor, location, new EquatableArray<string>(messageArguments));
    }

    /// <summary>
    /// Builds the reportable diagnostic, placing it in the matching syntax tree of <paramref name="compilation"/>.
    /// </summary>
    public Diagnostic ToDiagnostic(Compilation compilation)
    {
        var location = Location?.ToLocation(compilation) ?? Microsoft.CodeAnalysis.Location.None;
        var messageArguments = MessageArguments.Cast<object?>().ToArray();
        return Diagnostic.Create(Descriptor, location, messageArguments);
    }
}

/// <summary>
/// A source location as plain values: the file path and spans that <see cref="Location"/> would otherwise reference
/// through its syntax tree, plus the text at that span so the tree can be identified again.
/// </summary>
/// <param name="FilePath">Path of the syntax tree the location is in.</param>
/// <param name="TextSpan">Character span in that tree.</param>
/// <param name="LineSpan">Line and column span, used when the tree is no longer part of the compilation.</param>
/// <param name="LocatedText">The source text at <paramref name="TextSpan"/>, such as the field name.</param>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan, string LocatedText)
{
    /// <summary>
    /// Captures <paramref name="location"/>, or returns <see langword="null"/> when it is not in source.
    /// </summary>
    public static LocationInfo? From(Location? location, CancellationToken cancellationToken)
    {
        var syntaxTree = location?.SourceTree;
        if (location is null || syntaxTree is null)
        {
            return null;
        }
        var locatedText = syntaxTree.GetText(cancellationToken).ToString(location.SourceSpan);
        return new LocationInfo(syntaxTree.FilePath, location.SourceSpan, location.GetLineSpan().Span, locatedText);
    }

    /// <summary>
    /// Recreates the location inside the tree of <paramref name="compilation"/> it was captured from.
    /// </summary>
    /// <remarks>
    /// A location inside a compilation tree is what lets <c>#pragma warning disable</c> and per-file <c>.editorconfig</c>
    /// severities apply to the diagnostic; a location built from only a path and span does not support either. When no tree
    /// matches, the diagnostic is still reported at the path and line.
    /// </remarks>
    public Location ToLocation(Compilation compilation)
    {
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (IsCapturedFrom(syntaxTree))
            {
                return Location.Create(syntaxTree, TextSpan);
            }
        }
        return Location.Create(FilePath, TextSpan, LineSpan);
    }

    //the path alone is not unique: compilations built in memory, such as in tests, often give several trees the same
    //(empty) path, so the text at the span must match as well
    private bool IsCapturedFrom(SyntaxTree syntaxTree)
    {
        var isSamePath = string.Equals(syntaxTree.FilePath, FilePath, StringComparison.Ordinal);
        var spanFitsTree = TextSpan.End <= syntaxTree.Length;
        if (!isSamePath || !spanFitsTree)
        {
            return false;
        }
        var textAtSpan = syntaxTree.GetText().ToString(TextSpan);
        return string.Equals(textAtSpan, LocatedText, StringComparison.Ordinal);
    }
}
