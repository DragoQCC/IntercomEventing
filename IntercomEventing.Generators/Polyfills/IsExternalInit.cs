// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets the netstandard2.0 generator declare records and <see langword="init"/> accessors; the type ships in .NET 5 and
/// later, but not in netstandard2.0.
/// </summary>
internal static class IsExternalInit
{
}
