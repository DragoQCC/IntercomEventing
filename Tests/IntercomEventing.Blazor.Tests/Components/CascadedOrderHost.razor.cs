using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class CascadedOrderHost
{
    [Parameter]
    public IIntercomEvent<EventCall<OrderPlaced>>? OrderEvent { get; set; }
}
