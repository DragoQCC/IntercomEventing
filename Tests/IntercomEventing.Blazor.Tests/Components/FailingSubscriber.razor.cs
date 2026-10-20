using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class FailingSubscriber
{
    [Parameter]
    [EditorRequired]
    public IIntercomEvent<EventCall<string>> Source { get; set; } = default!;

    [Parameter]
    public bool HandleInComponent { get; set; }

    protected string HandledMessage { get; private set; } = "nothing handled";

    protected override void OnInitialized()
    {
        if (HandleInComponent)
        {
            //runs on the renderer and re-renders, so it can update component state directly
            Events.Subscribe(Source, ThrowForCall, onException: (exception, call) => HandledMessage = $"handled: {exception.Message}");
        }
        else
        {
            Events.Subscribe(Source, ThrowForCall);
        }
    }

    private static void ThrowForCall(EventCall<string> call)
    {
        throw new InvalidOperationException($"handler failed for {call.Payload}");
    }
}
