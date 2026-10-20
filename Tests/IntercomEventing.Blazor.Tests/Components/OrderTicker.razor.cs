using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class OrderTicker : IAsyncDisposable
{
    private readonly ComponentEventSubscriptions _events;
    private EventSubscription<EventCall<OrderPlaced>>? _orderSubscription;

    //the subscribe-only view: a cascaded IntercomEvent<OrderPlaced> matches it because it is assignable to this type
    [CascadingParameter]
    public IIntercomEvent<EventCall<OrderPlaced>>? OrderPlaced { get; set; }

    public EventSubscription<EventCall<OrderPlaced>>? OrderSubscription => _orderSubscription;

    protected int OrderCount { get; private set; }

    protected decimal LastTotal { get; private set; }

    public OrderTicker()
    {
        //InvokeAsync, StateHasChanged, DispatchExceptionAsync and RendererInfo are protected, so the component hands them over
        _events = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
    }

    public ValueTask DisposeAsync()
    {
        return _events.DisposeAsync();
    }

    protected override void OnParametersSet()
    {
        //a cascading value can change between renders; this keeps one subscription on whichever event is current
        _orderSubscription = _events.Resubscribe(_orderSubscription, OrderPlaced, OnOrderPlaced);
    }

    private void OnOrderPlaced(EventCall<OrderPlaced> call)
    {
        OrderCount++;
        LastTotal = call.Payload.Total;
    }
}
