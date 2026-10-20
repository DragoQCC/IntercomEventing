using System.Runtime.CompilerServices;
using IntercomEventing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

//IServiceCollection extensions conventionally live in its own namespace, so AddIntercomEventing is found without an extra using
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for applications that use <see cref="IServiceCollection"/>.
/// </summary>
public static class IntercomEventingServiceCollectionExtensions
{
    private static readonly Lock s_defaultOptionsLock = new();

    //stands for every AddIntercomEventing(EventingOptions) call: options applied while services are registered are never released
    private static readonly object s_registrationClaim = new();

    //who applied s_appliedDefaultOptions (or equal options since): s_registrationClaim and/or live service providers
    //(ContainerDefaultOptions); once nothing claims them, the next AddIntercomEventing counts as the first
    private static readonly HashSet<object> s_defaultOptionsClaims = [];

    //the options that AddIntercomEventing applied to EventingOptions.Default and that something still claims; later applies
    //must pass equal options
    private static EventingOptions? s_appliedDefaultOptions;

    /// <summary>
    /// Sets <see cref="EventingOptions.Default"/> for every event created without its own options, and registers
    /// <see cref="EventingOptions"/> as a singleton so services can read the defaults.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Events read the default at each raise, so events created before this call (for example in field
    /// initializers) still pick it up. Events constructed with explicit <see cref="EventingOptions"/> are not affected.</description></item>
    /// <item><description>The default is process-wide. A second call with options equal to the first (record equality) is a
    /// no-op; a second call with different options throws <see cref="InvalidOperationException"/> instead of silently
    /// replacing the first host's settings.</description></item>
    /// <item><description>Equality compares the <see cref="EventingOptions.DefaultExceptionHandler"/> delegate and the
    /// <see cref="EventingOptions.TimeProvider"/> instance, so the same configuration code run twice with a lambda that captures
    /// variables (a logger, for example) or with a new <see cref="TimeProvider"/> builds unequal options. When one process
    /// builds several hosts (integration tests, for example), pass one shared <see cref="EventingOptions"/> instance.</description></item>
    /// <item><description>Passing <see langword="null"/> (or nothing) leaves the default unchanged and only registers the
    /// singleton, so libraries can call this to make sure the service exists.</description></item>
    /// <item><description>To build the options from the application's services, such as an <c>ILogger</c> for
    /// <see cref="EventingOptions.DefaultExceptionHandler"/>, use the
    /// <see cref="AddIntercomEventing(IServiceCollection, Func{IServiceProvider, EventingOptions})"/> overload.</description></item>
    /// <item><description>Setting <see cref="EventingOptions.Default"/> directly is not checked and always wins.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="defaultOptions">The process-wide defaults; <see langword="null"/> keeps the current defaults.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An earlier call applied different options.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddIntercomEventing(new EventingOptions
    /// {
    ///     DispatchMode = DispatchMode.Parallel,
    ///     MaxConcurrentHandlers = 32,
    ///     DefaultExceptionHandler = failure => Console.Error.WriteLine($"{failure.EventName} {failure.Stage}: {failure.Exception.Message}"),
    /// });
    /// </code>
    /// </example>
    //keeps AddIntercomEventing(null) bound to this overload: a null literal also converts to the factory overload's parameter
    [OverloadResolutionPriority(1)]
    public static IServiceCollection AddIntercomEventing(this IServiceCollection services, EventingOptions? defaultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (defaultOptions is not null)
        {
            ApplyProcessDefaultOptions(defaultOptions, s_registrationClaim);
        }
        RegisterEventingOptionsService(services);
        return services;
    }

    /// <summary>
    /// Sets <see cref="EventingOptions.Default"/> from options built with the application's services, for example to send
    /// failures to an <c>ILogger</c>, and registers <see cref="EventingOptions"/> as a singleton.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>When it applies.</b> The factory runs once per service provider, the first time the provider
    /// resolves <see cref="EventingOptions"/>. The Generic Host, ASP.NET Core and Blazor Server do that when the host starts,
    /// before any hosted service's <c>StartAsync</c>. Without a host (Blazor WebAssembly, or a provider from
    /// <c>BuildServiceProvider</c>), resolve <see cref="EventingOptions"/> once after building the provider.</description></item>
    /// <item><description>Until then events use the previous <see cref="EventingOptions.Default"/>; code that raises events
    /// between <c>Build()</c> and <c>Run()</c> does not see these options yet.</description></item>
    /// <item><description>The factory receives the root provider: resolve singletons such as <c>ILoggerFactory</c>. It must
    /// not resolve <see cref="EventingOptions"/>.</description></item>
    /// <item><description><b>Conflicts.</b> The rule of the other overload applies when the options are applied: equal
    /// options are a no-op, different ones throw <see cref="InvalidOperationException"/> from the host start (or the
    /// resolve). Each provider runs the factory, and a handler that captures that provider's logger makes the options
    /// unequal, so two hosts running at the same time in one process conflict.</description></item>
    /// <item><description><b>Disposal.</b> Disposing the provider gives up its claim on the default, so a host started
    /// afterwards may apply different options. <see cref="EventingOptions.Default"/> keeps these options until then, so
    /// failures reported while the provider's services are disposed still reach the handler.</description></item>
    /// <item><description>Calling this again on the same service collection with the same factory is a no-op; a different
    /// factory throws.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="defaultOptionsFactory">Builds the process-wide defaults from the provider; must not return <see langword="null"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="defaultOptionsFactory"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">This service collection already has a different options factory. When
    /// the options are applied: the factory returned <see langword="null"/>, or different options are already applied by an
    /// <c>AddIntercomEventing</c> registration or by a provider that has not been disposed.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddIntercomEventing(services =>
    /// {
    ///     var logger = services.GetRequiredService&lt;ILoggerFactory&gt;().CreateLogger("IntercomEventing");
    ///     return new EventingOptions
    ///     {
    ///         MaxConcurrentHandlers = 32,
    ///         DefaultExceptionHandler = failure => logger.LogError(failure.Exception, "{EventName} {Stage} failed", failure.EventName, failure.Stage),
    ///     };
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddIntercomEventing(this IServiceCollection services, Func<IServiceProvider, EventingOptions> defaultOptionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(defaultOptionsFactory);
        RegisterDefaultOptionsFactory(services, defaultOptionsFactory);
        //one per provider: creating it runs the factory and applies the options
        services.TryAddSingleton(static provider =>
        {
            var factoryRegistration = provider.GetRequiredService<ContainerDefaultOptions.FactoryRegistration>();
            return new ContainerDefaultOptions(provider, factoryRegistration.Factory);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ContainerDefaultOptions.ApplyAtHostStart>());
        RegisterEventingOptionsService(services);
        return services;
    }

    /// <summary>
    /// Forgets the options applied by earlier <see cref="AddIntercomEventing(IServiceCollection, EventingOptions)"/> calls and
    /// options factories, and restores the built-in <see cref="EventingOptions.Default"/>, so tests in one process can each
    /// configure their own defaults.
    /// </summary>
    internal static void ResetProcessDefaultOptionsForTesting()
    {
        lock (s_defaultOptionsLock)
        {
            s_appliedDefaultOptions = null;
            s_defaultOptionsClaims.Clear();
            EventingOptions.Default = new EventingOptions();
        }
    }

    /// <summary>
    /// Makes <paramref name="defaultOptions"/> the process-wide default when nothing claims an earlier one, or checks that it
    /// equals the claimed one. Either way <paramref name="claimant"/> now claims the applied options.
    /// </summary>
    /// <exception cref="InvalidOperationException">Different options are claimed.</exception>
    internal static void ApplyProcessDefaultOptions(EventingOptions defaultOptions, object claimant)
    {
        lock (s_defaultOptionsLock)
        {
            if (s_appliedDefaultOptions is null)
            {
                EventingOptions.Default = defaultOptions;
                s_appliedDefaultOptions = defaultOptions;
                s_defaultOptionsClaims.Add(claimant);
                return;
            }
            //record equality: the same settings, the same exception handler delegate and the same TimeProvider instance
            var isIdenticalRepeat = s_appliedDefaultOptions.Equals(defaultOptions);
            if (!isIdenticalRepeat)
            {
                throw new InvalidOperationException(
                    "AddIntercomEventing was already called with different EventingOptions. EventingOptions.Default is process-wide, " +
                    "so a second host or test fixture would silently change the first one's events. Pass equal options, give " +
                    "the events of the second host their own EventingOptions, or, when the first options came from an " +
                    "AddIntercomEventing factory, dispose that host before starting the next one.");
            }
            //the applied options stay claimed for as long as anything that applied equal options is still alive
            s_defaultOptionsClaims.Add(claimant);
        }
    }

    /// <summary>
    /// Removes <paramref name="claimant"/>'s claim. When it was the last one, the next <c>AddIntercomEventing</c> counts as
    /// the first; <see cref="EventingOptions.Default"/> keeps its value until then.
    /// </summary>
    internal static void ReleaseProcessDefaultOptions(object claimant)
    {
        lock (s_defaultOptionsLock)
        {
            var wasClaimant = s_defaultOptionsClaims.Remove(claimant);
            var isUnclaimed = s_defaultOptionsClaims.Count == 0;
            if (wasClaimant && isUnclaimed)
            {
                s_appliedDefaultOptions = null;
            }
        }
    }

    /// <summary>
    /// Keeps one options factory per service collection: the same factory again is a no-op, a different one throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The collection already has a different factory.</exception>
    private static void RegisterDefaultOptionsFactory(IServiceCollection services, Func<IServiceProvider, EventingOptions> defaultOptionsFactory)
    {
        var existingRegistration = services
            .Where(descriptor => descriptor.ServiceType == typeof(ContainerDefaultOptions.FactoryRegistration))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ContainerDefaultOptions.FactoryRegistration>()
            .FirstOrDefault();
        if (existingRegistration is null)
        {
            services.AddSingleton(new ContainerDefaultOptions.FactoryRegistration(defaultOptionsFactory));
            return;
        }
        //Delegate.Equals: the same method on the same target, so a non-capturing lambda or a static method passed again is a repeat
        var isIdenticalRepeat = existingRegistration.Factory.Equals(defaultOptionsFactory);
        if (!isIdenticalRepeat)
        {
            throw new InvalidOperationException(
                "AddIntercomEventing was already called on this service collection with a different options factory. " +
                "EventingOptions.Default is process-wide, so the application can have only one; register it once.");
        }
    }

    private static void RegisterEventingOptionsService(IServiceCollection services)
    {
        services.TryAddSingleton<EventingOptions>(ResolveEventingOptionsService);
    }

    //resolved on first use: a provider with an options factory applies the factory's options now, and any other provider
    //gets EventingOptions.Default as it is then, so it matches options applied after this registration
    private static EventingOptions ResolveEventingOptionsService(IServiceProvider provider)
    {
        var containerDefaultOptions = provider.GetService<ContainerDefaultOptions>();
        if (containerDefaultOptions is not null)
        {
            return containerDefaultOptions.Options;
        }
        return EventingOptions.Default;
    }
}
