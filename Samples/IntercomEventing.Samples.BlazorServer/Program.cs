using IntercomEventing.Samples.BlazorServer.Components;
using IntercomEventing.Samples.BlazorServer.Features.Chat;
using IntercomEventing.Samples.BlazorServer.Features.Search;
using IntercomEventing.Samples.BlazorServer.Features.ServerRack;
using IntercomEventing.Samples.BlazorServer.Features.Telemetry;
using IntercomEventing.Samples.BlazorServer.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

//singletons are shared by every browser tab (each tab is its own circuit), so their events reach every connected user.
//the container disposes them asynchronously at shutdown, which disposes their events
builder.Services.AddSingleton<RackInventory>();
builder.Services.AddSingleton<ChatRoom>();
builder.Services.AddSingleton<SensorHub>();
//a subscriber that lives as long as the app and is not a component
builder.Services.AddHostedService<ChatBot>();

//scoped services live for one circuit: one search box, and one visitor name, per browser tab
builder.Services.AddScoped<ProductSearch>();
builder.Services.AddScoped<CircuitVisitor>();

var app = builder.Build();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
