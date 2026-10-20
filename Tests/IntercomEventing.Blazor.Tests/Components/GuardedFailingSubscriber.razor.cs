using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class GuardedFailingSubscriber
{
    [Parameter]
    [EditorRequired]
    public IIntercomEvent<EventCall<string>> Source { get; set; } = default!;

    [Parameter]
    public bool HandleInComponent { get; set; }
}
