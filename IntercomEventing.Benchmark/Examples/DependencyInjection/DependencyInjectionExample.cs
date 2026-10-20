using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntercomEventing.Benchmark.Examples.DependencyInjection;

/// <summary>
/// <c>AddIntercomEventing</c> in a generic host: an options factory builds the process-wide <see cref="EventingOptions.Default"/>
/// from the container, so failures go to an <see cref="ILogger"/>; producers are ordinary services and subscribers can be
/// hosted services.
/// </summary>
internal sealed class DependencyInjectionExample : IExample
{
    public string Name => "DependencyInjection";

    public string Summary => "AddIntercomEventing with an ILogger for failures, a generic host and a hosted subscriber";

    public async Task RunAsync()
    {
        ExampleCheck.Step("Building a host with AddIntercomEventing, a singleton producer and a hosted subscriber");
        var builder = Host.CreateApplicationBuilder();
        //keeps the example output to the event messages, one line per log entry
        var logEntries = new RecordingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logEntries);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddIntercomEventing(services =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IntercomEventing");
            return new EventingOptions
            {
                DispatchMode = DispatchMode.Parallel,
                MaxConcurrentHandlers = 32,
                StartNextEventHandlerAfter = TimeSpan.FromSeconds(5),
                DefaultExceptionHandler = failure => logger.LogError(failure.Exception, "{EventName} {Stage} failed", failure.EventName, failure.Stage),
            };
        });
        builder.Services.AddSingleton<ShippingService>();
        builder.Services.AddSingleton<CustomerNotifier>();
        builder.Services.AddHostedService(services => services.GetRequiredService<CustomerNotifier>());

        var host = builder.Build();
        var shipping = host.Services.GetRequiredService<ShippingService>();
        var notifier = host.Services.GetRequiredService<CustomerNotifier>();
        try
        {
            ExampleCheck.That(shipping.OrderShipped.Options.MaxConcurrentHandlers != 32, "before the host starts, events still use the previous default");
            await host.StartAsync();

            var registeredOptions = host.Services.GetRequiredService<EventingOptions>();
            ExampleCheck.That(ReferenceEquals(registeredOptions, EventingOptions.Default), "starting the host applied the factory's options as the process-wide default");
            ExampleCheck.That(shipping.OrderShipped.Options.MaxConcurrentHandlers == 32, "an event created without options uses the configured default");

            ExampleCheck.Step("Shipping two orders, one with a carrier the notifier cannot handle");
            await shipping.ShipAsync(1001, "DHL");
            await shipping.ShipAsync(1002, "Carrier Pigeon");
            ExampleCheck.That(notifier.NotifiedOrders.SequenceEqual([1001]), "the hosted subscriber handled the supported shipment");
            var loggedFailure = logEntries.Entries.Single();
            var isLoggedAsError = loggedFailure.Level == LogLevel.Error && loggedFailure.Exception is NotSupportedException;
            ExampleCheck.That(isLoggedAsError, "the failure reached the ILogger that the options factory resolved");

            ExampleCheck.Step("Stopping and disposing the host");
            await host.StopAsync();
            ExampleCheck.That(shipping.OrderShipped.SubscriberCount == 0, "the hosted service unsubscribed in StopAsync");
        }
        finally
        {
            await DisposeHostAsync(host);
        }
        ExampleCheck.That(shipping.OrderShipped.IsDisposed, "disposing the host disposed the singleton producer and its event");
    }

    /// <summary>
    /// <see cref="IHost"/> declares only <see cref="IDisposable"/>, but the host that <c>Build()</c> returns is also
    /// <see cref="IAsyncDisposable"/>. Disposing it asynchronously lets the container await async-only singletons such as
    /// <see cref="ShippingService"/>, where a synchronous dispose would block a thread on them.
    /// </summary>
    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposableHost)
        {
            await asyncDisposableHost.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }
}
