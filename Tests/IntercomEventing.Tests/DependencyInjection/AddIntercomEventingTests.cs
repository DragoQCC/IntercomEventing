using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntercomEventing.Tests.DependencyInjection;

/// <summary>
/// <c>AddIntercomEventing</c>: applies the process-wide <see cref="EventingOptions.Default"/> once, accepts identical repeats,
/// rejects a conflicting second configuration and registers the options as a singleton.
/// </summary>
/// <remarks>
/// Every test here changes process-wide state, so the class runs on its own and resets the defaults around each test.
/// </remarks>
[NotInParallel]
public class AddIntercomEventingTests
{
    [Before(Test)]
    public void ForgetEarlierConfigurations()
    {
        IntercomEventingServiceCollectionExtensions.ResetProcessDefaultOptionsForTesting();
    }

    [After(Test)]
    public void RestoreTheBuiltInDefaults()
    {
        IntercomEventingServiceCollectionExtensions.ResetProcessDefaultOptionsForTesting();
    }

    [Test]
    public async Task FirstCall_SetsTheProcessDefault()
    {
        var appliedOptions = new EventingOptions { MaxConcurrentHandlers = 7 };

        new ServiceCollection().AddIntercomEventing(appliedOptions);

        await Assert.That(EventingOptions.Default).IsSameReferenceAs(appliedOptions);
    }

    [Test]
    public async Task RepeatedCallsWithIdenticalOptions_AreNoOps()
    {
        //core-port fix: the same configuration run again (a second host using the same settings) must not fail
        var appliedOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var services = new ServiceCollection();
        services.AddIntercomEventing(appliedOptions);

        await Assert.That(() => services.AddIntercomEventing(appliedOptions)).ThrowsNothing();
        await Assert.That(() => services.AddIntercomEventing(appliedOptions with { })).ThrowsNothing();
        await Assert.That(() => new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = 7 })).ThrowsNothing();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(appliedOptions);
    }

    [Test]
    public async Task CallWithoutOptions_LeavesTheDefaultUnchanged()
    {
        var appliedOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        new ServiceCollection().AddIntercomEventing(appliedOptions);

        new ServiceCollection().AddIntercomEventing();

        await Assert.That(EventingOptions.Default).IsSameReferenceAs(appliedOptions);
    }

    [Test]
    public async Task SecondCallWithDifferentOptions_ThrowsAndKeepsTheFirst()
    {
        //A9: in 1.2.0 a second AddIntercomEventing (a second host or test fixture) silently overrode the first host's options
        var firstOptions = new EventingOptions { DispatchMode = DispatchMode.Sequential };
        new ServiceCollection().AddIntercomEventing(firstOptions);

        await Assert.That(() => new ServiceCollection().AddIntercomEventing(new EventingOptions { DispatchMode = DispatchMode.Parallel }))
            .Throws<InvalidOperationException>();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(firstOptions);
    }

    [Test]
    public async Task ADifferentExceptionHandlerDelegate_CountsAsDifferentOptions()
    {
        //documented: equality compares the delegate, so share one EventingOptions instance between hosts
        var firstFailures = new FailureRecorder();
        var secondFailures = new FailureRecorder();
        new ServiceCollection().AddIntercomEventing(new EventingOptions { DefaultExceptionHandler = firstFailures.Record });

        await Assert.That(() => new ServiceCollection().AddIntercomEventing(new EventingOptions { DefaultExceptionHandler = secondFailures.Record }))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RepeatedCalls_RegisterTheOptionsOnce()
    {
        var appliedOptions = new EventingOptions();
        var services = new ServiceCollection();

        services.AddIntercomEventing(appliedOptions);
        services.AddIntercomEventing(appliedOptions);
        services.AddIntercomEventing();

        var registrations = services.Count(descriptor => descriptor.ServiceType == typeof(EventingOptions));
        await Assert.That(registrations).IsEqualTo(1);
    }

    [Test]
    public async Task RegisteredOptions_ResolveToTheDefaultInEffectAtFirstUse()
    {
        //registered before the options are applied (a library calling AddIntercomEventing() first) and still correct
        var services = new ServiceCollection();
        services.AddIntercomEventing();
        var appliedOptions = new EventingOptions { MaxConcurrentHandlers = 3 };
        new ServiceCollection().AddIntercomEventing(appliedOptions);

        await using var provider = services.BuildServiceProvider();
        var resolvedOptions = provider.GetRequiredService<EventingOptions>();

        await Assert.That(resolvedOptions).IsSameReferenceAs(appliedOptions);
    }

    [Test]
    public async Task ConcurrentConflictingCalls_ExactlyOneWins()
    {
        var winners = 0;
        var losers = 0;

        Parallel.For(0, 64, index =>
        {
            try
            {
                new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = index + 1 });
                Interlocked.Increment(ref winners);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref losers);
            }
        });

        await Assert.That(winners).IsEqualTo(1);
        await Assert.That(losers).IsEqualTo(63);
    }

    [Test]
    public async Task EventCreatedBeforeTheCall_UsesTheAppliedOptionsAtItsNextRaise()
    {
        //A9: events created in field initializers, before the host configured eventing, pick up the defaults at raise time
        var createdBeforeTheCall = new IntercomEvent<int>();
        Func<EventCall<int>, Task> failingHandler = _ => throw new InvalidOperationException("handler failure");
        createdBeforeTheCall.Subscribe(failingHandler);
        var failures = new FailureRecorder();

        new ServiceCollection().AddIntercomEventing(new EventingOptions { DefaultExceptionHandler = failures.Record });
        await createdBeforeTheCall.RaiseAsync(1);

        await Assert.That(failures.Count).IsEqualTo(1);
        await createdBeforeTheCall.DisposeAsync();
    }

    [Test]
    public async Task EventWithItsOwnOptions_IsNotAffected()
    {
        var ownFailures = new FailureRecorder();
        var defaultFailures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = ownFailures.Record });
        Func<EventCall<int>, Task> failingHandler = _ => throw new InvalidOperationException("handler failure");
        evt.Subscribe(failingHandler);

        new ServiceCollection().AddIntercomEventing(new EventingOptions { DefaultExceptionHandler = defaultFailures.Record });
        await evt.RaiseAsync(1);

        await Assert.That(ownFailures.Count).IsEqualTo(1);
        await Assert.That(defaultFailures.Count).IsEqualTo(0);
        await evt.DisposeAsync();
    }

    [Test]
    public async Task SettingDefaultDirectly_IsNotCheckedAndWins()
    {
        new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = 7 });
        var directlySetOptions = new EventingOptions { MaxConcurrentHandlers = 9 };

        EventingOptions.Default = directlySetOptions;

        await Assert.That(EventingOptions.Default).IsSameReferenceAs(directlySetOptions);
    }

    [Test]
    public async Task ResetForTesting_RestoresTheBuiltInDefaultsAndAllowsANewConfiguration()
    {
        new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = 7 });

        IntercomEventingServiceCollectionExtensions.ResetProcessDefaultOptionsForTesting();
        var builtInDefaults = EventingOptions.Default;
        var nextOptions = new EventingOptions { MaxConcurrentHandlers = 8 };

        await Assert.That(builtInDefaults).IsEqualTo(new EventingOptions());
        await Assert.That(() => new ServiceCollection().AddIntercomEventing(nextOptions)).ThrowsNothing();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(nextOptions);
    }

    [Test]
    public async Task NullServices_AreRejected()
    {
        IServiceCollection? noServices = null;

        await Assert.That(() => noServices!.AddIntercomEventing()).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task NullLiteral_StillBindsToTheOptionsOverload()
    {
        //OverloadResolutionPriority: without it this call would be ambiguous once the factory overload exists
        var appliedOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        new ServiceCollection().AddIntercomEventing(appliedOptions);

        new ServiceCollection().AddIntercomEventing(null);

        await Assert.That(EventingOptions.Default).IsSameReferenceAs(appliedOptions);
    }

    [Test]
    public async Task OptionsFactory_IsAppliedWhenEventingOptionsIsFirstResolved()
    {
        var builtInDefaults = EventingOptions.Default;
        var factoryOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var services = new ServiceCollection();
        services.AddIntercomEventing(_ => factoryOptions);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(EventingOptions.Default).IsSameReferenceAs(builtInDefaults);
        var resolvedOptions = provider.GetRequiredService<EventingOptions>();

        await Assert.That(resolvedOptions).IsSameReferenceAs(factoryOptions);
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(factoryOptions);
    }

    [Test]
    public async Task OptionsFactory_RunsOncePerProvider()
    {
        var factoryRuns = 0;
        var factoryOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var services = new ServiceCollection();
        services.AddIntercomEventing(_ =>
        {
            Interlocked.Increment(ref factoryRuns);
            return factoryOptions;
        });
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<EventingOptions>();
        provider.GetRequiredService<EventingOptions>();
        provider.GetServices<IHostedService>().ToList();

        await Assert.That(factoryRuns).IsEqualTo(1);
    }

    [Test]
    public async Task OptionsFactory_CanSendFailuresToAServiceFromTheProvider()
    {
        //the reason for the factory overload: an ILogger (here a recorder) only exists once the provider is built
        var services = new ServiceCollection();
        services.AddSingleton<FailureRecorder>();
        services.AddIntercomEventing(container =>
        {
            var failureService = container.GetRequiredService<FailureRecorder>();
            return new EventingOptions { DefaultExceptionHandler = failureService.Record };
        });
        await using var provider = services.BuildServiceProvider();
        var evt = new IntercomEvent<int>();
        Func<EventCall<int>, Task> failingHandler = _ => throw new InvalidOperationException("handler failure");
        evt.Subscribe(failingHandler);

        provider.GetRequiredService<EventingOptions>();
        await evt.RaiseAsync(1);

        var recordedFailures = provider.GetRequiredService<FailureRecorder>();
        await Assert.That(recordedFailures.Count).IsEqualTo(1);
        await evt.DisposeAsync();
    }

    [Test]
    public async Task OptionsFactory_IsAppliedBeforeAnyHostedServiceStarts()
    {
        //the host constructs every hosted service, then starts them in registration order; this probe is registered first
        var factoryOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var probe = new DefaultOptionsProbe();
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService>(probe);
        services.AddIntercomEventing(_ => factoryOptions);
        await using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Assert.That(probe.DefaultAtStart).IsSameReferenceAs(factoryOptions);
    }

    [Test]
    public async Task OptionsFactory_LibraryCallWithoutOptionsFirst_StillResolvesTheFactoryOptions()
    {
        //a library registered EventingOptions before the application added its factory
        var factoryOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var services = new ServiceCollection();
        services.AddIntercomEventing();
        services.AddIntercomEventing(_ => factoryOptions);
        await using var provider = services.BuildServiceProvider();

        var resolvedOptions = provider.GetRequiredService<EventingOptions>();

        await Assert.That(resolvedOptions).IsSameReferenceAs(factoryOptions);
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(factoryOptions);
    }

    [Test]
    public async Task OptionsFactory_ReturningNull_ThrowsWhenApplied()
    {
        var services = new ServiceCollection();
        services.AddIntercomEventing(_ => null!);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<EventingOptions>()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NullOptionsFactory_IsRejected()
    {
        Func<IServiceProvider, EventingOptions>? noFactory = null;

        await Assert.That(() => new ServiceCollection().AddIntercomEventing(noFactory!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SameFactoryTwiceOnOneCollection_IsANoOp()
    {
        Func<IServiceProvider, EventingOptions> optionsFactory = _ => new EventingOptions();
        var services = new ServiceCollection();

        services.AddIntercomEventing(optionsFactory);
        services.AddIntercomEventing(optionsFactory);

        var hostedServiceRegistrations = services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService));
        var optionsRegistrations = services.Count(descriptor => descriptor.ServiceType == typeof(EventingOptions));
        await Assert.That(hostedServiceRegistrations).IsEqualTo(1);
        await Assert.That(optionsRegistrations).IsEqualTo(1);
    }

    [Test]
    public async Task DifferentFactoryOnTheSameCollection_Throws()
    {
        var services = new ServiceCollection();
        services.AddIntercomEventing(_ => new EventingOptions { MaxConcurrentHandlers = 7 });

        await Assert.That(() => services.AddIntercomEventing(_ => new EventingOptions { MaxConcurrentHandlers = 8 }))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task OptionsFactory_DifferentOptionsWhileAnotherProviderIsAlive_ThrowWhenApplied()
    {
        var firstOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        await using var firstProvider = BuildProviderWithFactoryOptions(firstOptions);
        await using var secondProvider = BuildProviderWithFactoryOptions(new EventingOptions { MaxConcurrentHandlers = 8 });
        firstProvider.GetRequiredService<EventingOptions>();

        await Assert.That(() => secondProvider.GetRequiredService<EventingOptions>()).Throws<InvalidOperationException>();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(firstOptions);
    }

    [Test]
    public async Task DisposingTheProvider_ReleasesItsClaimButKeepsTheDefault()
    {
        //Default stays so failures reported while the provider's other services are disposed still reach the handler
        var factoryOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var provider = BuildProviderWithFactoryOptions(factoryOptions);
        provider.GetRequiredService<EventingOptions>();

        await provider.DisposeAsync();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(factoryOptions);

        //the next host (a sequential integration test, for example) may apply different options
        var nextOptions = new EventingOptions { MaxConcurrentHandlers = 8 };
        await Assert.That(() => new ServiceCollection().AddIntercomEventing(nextOptions)).ThrowsNothing();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(nextOptions);
    }

    [Test]
    public async Task EqualFactoryOptionsFromTwoProviders_StayClaimedUntilBothAreDisposed()
    {
        var sharedOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        var firstProvider = BuildProviderWithFactoryOptions(sharedOptions);
        var secondProvider = BuildProviderWithFactoryOptions(sharedOptions);
        firstProvider.GetRequiredService<EventingOptions>();
        secondProvider.GetRequiredService<EventingOptions>();

        await firstProvider.DisposeAsync();
        await Assert.That(() => new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = 8 }))
            .Throws<InvalidOperationException>();

        await secondProvider.DisposeAsync();
        await Assert.That(() => new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = 8 }))
            .ThrowsNothing();
    }

    [Test]
    public async Task OptionsAppliedAtRegistration_AreNotReleasedByAProviderWithEqualOptions()
    {
        var registeredOptions = new EventingOptions { MaxConcurrentHandlers = 7 };
        new ServiceCollection().AddIntercomEventing(registeredOptions);
        var provider = BuildProviderWithFactoryOptions(registeredOptions);
        provider.GetRequiredService<EventingOptions>();

        await provider.DisposeAsync();

        await Assert.That(() => new ServiceCollection().AddIntercomEventing(new EventingOptions { MaxConcurrentHandlers = 8 }))
            .Throws<InvalidOperationException>();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(registeredOptions);
    }

    private static ServiceProvider BuildProviderWithFactoryOptions(EventingOptions factoryOptions)
    {
        var services = new ServiceCollection();
        services.AddIntercomEventing(_ => factoryOptions);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A hosted service that records <see cref="EventingOptions.Default"/> when it is started.
    /// </summary>
    private sealed class DefaultOptionsProbe : IHostedService
    {
        public EventingOptions? DefaultAtStart { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            DefaultAtStart = EventingOptions.Default;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
