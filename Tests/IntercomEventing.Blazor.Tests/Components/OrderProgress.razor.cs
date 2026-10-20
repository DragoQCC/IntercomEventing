using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class OrderProgress
{
    [Inject]
    private OrderBook Orders { get; set; } = default!;

    [Inject]
    private HandlerGate Gate { get; set; } = default!;

    [Inject]
    private RendererProbe Probe { get; set; } = default!;

    protected string Status { get; private set; } = "idle";

    protected override void OnInitialized()
    {
        Events.Subscribe(Orders.OrderPlaced, OnOrderPlacedAsync);
    }

    private async Task OnOrderPlacedAsync(EventCall<OrderPlaced> call)
    {
        Probe.RecordHandlerCall();
        Status = $"loading order {call.Payload.OrderNumber}";
        await Gate.WaitAsync();
        //still on the renderer after the await, so the second render shows this
        Probe.RecordHandlerCall();
        Status = $"order {call.Payload.OrderNumber} loaded";
    }
}
