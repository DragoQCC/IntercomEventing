using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class OrderDashboard
{
    [Inject]
    private RendererProbe Probe { get; set; } = default!;

    [Parameter]
    public IIntercomEvent<EventCall<OrderPlaced>>? OrderEvent { get; set; }

    [Parameter]
    public bool ShowListeners { get; set; } = true;

    protected int? LastOrderNumber { get; private set; }

    protected decimal TotalSeen { get; private set; }

    //invoked through an EventCallback, so this component re-renders without calling StateHasChanged
    private void OnOrderPlaced(EventCall<OrderPlaced> call)
    {
        Probe.RecordHandlerCall();
        LastOrderNumber = call.Payload.OrderNumber;
    }

    private void AddToTotal(EventCall<OrderPlaced> call)
    {
        TotalSeen += call.Payload.Total;
    }
}
