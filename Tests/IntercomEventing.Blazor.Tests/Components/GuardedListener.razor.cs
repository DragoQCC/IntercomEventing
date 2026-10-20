using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class GuardedListener
{
    [Parameter]
    [EditorRequired]
    public IIntercomEvent<EventCall<string>> Source { get; set; } = default!;

    private static void ThrowFromCallback(EventCall<string> call)
    {
        throw new InvalidOperationException($"callback failed for {call.Payload}");
    }
}
