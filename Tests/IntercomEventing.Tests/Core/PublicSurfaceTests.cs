using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// The shape of the published core assembly: what it depends on and where its public types live.
/// </summary>
public class PublicSurfaceTests
{
    [Test]
    public async Task CoreAssembly_ReferencesOnlyTheSharedFramework()
    {
        //A2: 1.2.0 depended on HelpfulTypesAndExtensions, whose Microsoft.AspNetCore.App framework reference broke Blazor WebAssembly
        var referencedAssemblyNames = typeof(IntercomEventBase<>).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        //both Microsoft.Extensions assemblies are in Microsoft.NETCore.App on net11.0 (WebAssembly included); Hosting.Abstractions
        //supplies IHostedService for AddIntercomEventing's options factory
        Func<string, bool> isSharedFrameworkAssembly = name =>
            name == "System"
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name == "Microsoft.Extensions.DependencyInjection.Abstractions"
            || name == "Microsoft.Extensions.Hosting.Abstractions";
        await Assert.That(referencedAssemblyNames).IsNotEmpty();
        await Assert.That(referencedAssemblyNames).All(isSharedFrameworkAssembly);
    }

    [Test]
    public async Task PublicTypes_LiveInTheIntercomEventingNamespace()
    {
        //S14 (namespace part): the README's single `using IntercomEventing;` gave CS0246 in 1.2.0 because the types lived in
        //IntercomEventing.Features.Events; the README rewrite itself is documentation work outside this suite
        var publicTypes = typeof(IntercomEventBase<>).Assembly.GetExportedTypes();
        var registrationExtensions = typeof(IntercomEventingServiceCollectionExtensions);

        Func<Type, bool> isInTheIntercomEventingNamespace = type => type.Namespace == "IntercomEventing";
        var typesElsewhere = publicTypes
            .Where(type => !isInTheIntercomEventingNamespace(type))
            .ToArray();
        await Assert.That(publicTypes).IsNotEmpty();
        //AddIntercomEventing follows the IServiceCollection convention of living in Microsoft.Extensions.DependencyInjection
        await Assert.That(typesElsewhere).IsEquivalentTo([registrationExtensions]);
        await Assert.That(registrationExtensions.Namespace).IsEqualTo("Microsoft.Extensions.DependencyInjection");
    }
}
