using Microsoft.AspNetCore.Components;

namespace IntercomEventing.PackageConsumer;

/// <summary>
/// Uses each public piece a Blazor consumer reaches for: <see cref="IntercomEventing.Blazor.IntercomComponentBase"/>, its
/// <c>Events</c> helper, <c>&lt;EventListener&gt;</c>, and the generated event and view property.
/// </summary>
public partial class OrderCounter
{
    [Inject]
    private OrderBook Orders { get; set; } = default!;

    protected int OrderCount { get; private set; }

    protected decimal LastTotal { get; private set; }

    protected override void OnInitialized()
    {
        Events.Subscribe(Orders.OrderPlacedEvent, call => OrderCount++);
        //the generated native event member
        Orders.OrderPlaced += OnOrderPlacedAsync;
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Orders.OrderPlaced -= OnOrderPlacedAsync;
        return ValueTask.CompletedTask;
    }

    private void RecordTotal(EventCall<OrderPlaced> call)
    {
        LastTotal = call.Payload.Total;
    }

    private Task OnOrderPlacedAsync(EventCall<OrderPlaced> call)
    {
        return InvokeAsync(StateHasChanged);
    }
}
