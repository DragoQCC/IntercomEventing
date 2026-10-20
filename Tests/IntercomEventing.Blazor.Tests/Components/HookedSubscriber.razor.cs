using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class HookedSubscriber
{
    [Parameter]
    [EditorRequired]
    public IIntercomEvent<EventCall<string>> Source { get; set; } = default!;

    public bool UnsubscribeHookRan { get; private set; }

    protected bool IsSubscribed { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        var subscription = await Events.SubscribeAsync(Source, IgnoreCall, new()
        {
            OnSubscribed = _ => Task.CompletedTask,
            OnUnsubscribed = FailToUnsubscribe,
        });
        IsSubscribed = subscription is not null;
    }

    private static void IgnoreCall(EventCall<string> call)
    {
    }

    private Task FailToUnsubscribe(EventSubscription<EventCall<string>> subscription)
    {
        UnsubscribeHookRan = true;
        throw new InvalidOperationException("unsubscribe hook failed");
    }
}
