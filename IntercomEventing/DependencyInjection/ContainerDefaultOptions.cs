using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntercomEventing;

/// <summary>
/// The options that one service provider built with the factory passed to
/// <see cref="IntercomEventingServiceCollectionExtensions.AddIntercomEventing(IServiceCollection, Func{IServiceProvider, EventingOptions})"/>.
/// The provider creates this singleton the first time it resolves <see cref="EventingOptions"/> (or when the host starts),
/// which runs the factory once and applies its options to <see cref="EventingOptions.Default"/>.
/// </summary>
/// <remarks>
/// Disposing the provider gives up this provider's claim on the process-wide default, so another provider or
/// registration may apply different options. <see cref="EventingOptions.Default"/> itself is left as it is: the provider
/// disposes this singleton before services it created earlier, and failures those services report while they are disposed
/// should still reach the factory's exception handler.
/// </remarks>
internal sealed class ContainerDefaultOptions : IDisposable
{
    /// <summary>
    /// The options the factory returned, which are now <see cref="EventingOptions.Default"/> (or equal to it).
    /// </summary>
    public EventingOptions Options { get; }

    /// <summary>
    /// Runs <paramref name="defaultOptionsFactory"/> and applies its options as the process-wide default.
    /// </summary>
    /// <exception cref="InvalidOperationException">The factory returned <see langword="null"/>, or different options are
    /// already applied by an <c>AddIntercomEventing</c> registration or by another provider that is still alive.</exception>
    public ContainerDefaultOptions(IServiceProvider services, Func<IServiceProvider, EventingOptions> defaultOptionsFactory)
    {
        var options = defaultOptionsFactory(services);
        if (options is null)
        {
            throw new InvalidOperationException("The factory passed to AddIntercomEventing returned null; it must return the EventingOptions to apply.");
        }
        IntercomEventingServiceCollectionExtensions.ApplyProcessDefaultOptions(options, claimant: this);
        Options = options;
    }

    public void Dispose()
    {
        //releasing twice is harmless: the second call finds no claim to remove
        IntercomEventingServiceCollectionExtensions.ReleaseProcessDefaultOptions(claimant: this);
    }

    /// <summary>
    /// The factory passed to <c>AddIntercomEventing</c>, kept in the service collection so that a second call on the same
    /// collection can be compared with the first.
    /// </summary>
    internal sealed record FactoryRegistration(Func<IServiceProvider, EventingOptions> Factory);

    /// <summary>
    /// Applies the provider's options when the host starts, so apps on the Generic Host, ASP.NET Core or Blazor Server do not
    /// have to resolve <see cref="EventingOptions"/> themselves.
    /// </summary>
    /// <remarks>
    /// Taking <see cref="ContainerDefaultOptions"/> as a constructor dependency is what applies the options: the host
    /// constructs every hosted service before it starts any of them, so the options are in place before the first
    /// <see cref="IHostedService.StartAsync"/> runs (a hosted service that subscribes or starts a timer sees them).
    /// </remarks>
    internal sealed class ApplyAtHostStart : IHostedService
    {
        public ApplyAtHostStart(ContainerDefaultOptions appliedOptions)
        {
            //nothing to keep: creating appliedOptions already applied them
            _ = appliedOptions;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
