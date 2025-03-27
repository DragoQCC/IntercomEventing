using IntercomEventing.Features.Events;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing;

public static class IntercomEventingExtensions
{
    /// <summary>
    /// Adds the eventing system to the service collection
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configureOptions">An optional action to configure the eventing options, such as the sync type and max number of concurrent handlers</param>
    /// <returns></returns>
    public static IServiceCollection AddIntercomEventing(this IServiceCollection services, Action<EventingOptions>? configureOptions = null)
    {
        var eventingConfiguration = new EventingConfiguration(configureOptions);
        services.AddSingleton(eventingConfiguration);
        return services;
    }
}