using System.Text.Json.Serialization;
using IntercomEventing;
using IntercomEventing.Samples.WebApi.Features.Audit;
using IntercomEventing.Samples.WebApi.Features.Catalog;
using IntercomEventing.Samples.WebApi.Features.LiveFeed;
using IntercomEventing.Samples.WebApi.Features.Orders;
using IntercomEventing.Samples.WebApi.Features.Store;

var builder = WebApplication.CreateBuilder(args);

//sets EventingOptions.Default, which every event created without its own options reads at each raise. The factory runs when
//the host starts (before any hosted service's StartAsync), so the options can use the app's services, such as its logger
builder.Services.AddIntercomEventing(services =>
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IntercomEventing");
    return new EventingOptions
    {
        //handlers here call (simulated) mail servers and stores, so wait longer than the 1 second default before moving on
        StartNextEventHandlerAfter = TimeSpan.FromSeconds(2),
        //receives every handler failure that the subscription does not handle itself
        DefaultExceptionHandler = failure => logger.LogError(failure.Exception,
            "Event {EventName} failed in stage {Stage} (subscription {SubscriptionId})", failure.EventName, failure.Stage, failure.SubscriptionId),
    };
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

//producers: singletons that own their events. The container disposes them asynchronously at shutdown, which disposes the events
builder.Services.AddSingleton<StoreService>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<OrderService>();

//subscribers that live as long as the app are hosted services: they subscribe in StartAsync and unsubscribe in StopAsync.
//hosted services stop in reverse order, so AuditArchive (registered before AuditTrail) still receives the trail's final batch
builder.Services.AddHostedService<OrderConfirmationMailer>();
builder.Services.AddSingleton<PriceListPublisher>();
builder.Services.AddHostedService(services => services.GetRequiredService<PriceListPublisher>());
builder.Services.AddSingleton<AuditArchive>();
builder.Services.AddHostedService(services => services.GetRequiredService<AuditArchive>());
builder.Services.AddSingleton<AuditTrail>();
builder.Services.AddHostedService(services => services.GetRequiredService<AuditTrail>());

var app = builder.Build();

app.MapGet("/", () => "IntercomEventing Minimal API sample. IntercomEventing.Samples.WebApi.http lists the endpoints.");
app.MapStoreEndpoints();
app.MapCatalogEndpoints();
app.MapOrderEndpoints();
app.MapAuditEndpoints();
app.MapLiveFeedEndpoints();

app.Run();
