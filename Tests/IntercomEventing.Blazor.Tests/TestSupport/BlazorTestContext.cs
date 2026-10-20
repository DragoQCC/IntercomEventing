using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing.Blazor.Tests.TestSupport;

/// <summary>
/// Creates bUnit contexts with the sample services registered.
/// </summary>
public static class BlazorTestContext
{
    /// <summary>
    /// A context whose renderer reports itself as an interactive Blazor Server renderer.
    /// </summary>
    /// <param name="addServices">Registers further services; bUnit accepts registrations only before the renderer is first used.</param>
    public static BunitContext CreateInteractive(Action<IServiceCollection>? addServices = null)
    {
        var context = CreateWithoutRendererInfo(addServices);
        context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return context;
    }

    /// <summary>
    /// A context whose renderer has not been told its <see cref="RendererInfo"/>; reading it throws bUnit's MissingRendererInfoException.
    /// </summary>
    /// <param name="addServices">Registers further services; bUnit accepts registrations only before the renderer is first used.</param>
    public static BunitContext CreateWithoutRendererInfo(Action<IServiceCollection>? addServices = null)
    {
        var context = new BunitContext();
        context.Services.AddSingleton(new RendererProbe(() => context.Renderer.Dispatcher));
        context.Services.AddSingleton<UserDirectory>();
        context.Services.AddSingleton<OrderBook>();
        context.Services.AddSingleton<HandlerGate>();
        addServices?.Invoke(context.Services);
        return context;
    }
}
