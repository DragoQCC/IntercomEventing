using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class DisposalRecorder
{
    private readonly List<string> _disposalSteps = [];

    [Parameter]
    [EditorRequired]
    public IIntercomEvent<EventCall<string>> Source { get; set; } = default!;

    /// <summary>
    /// The cleanup methods in the order they ran, each with the subscriber count it saw.
    /// </summary>
    public IReadOnlyList<string> DisposalSteps => _disposalSteps;

    //the renderer never calls this itself: the component is IAsyncDisposable through its base class
    public void Dispose()
    {
        _disposalSteps.Add($"Dispose saw {Source.SubscriberCount} subscribers");
    }

    protected override void OnInitialized()
    {
        Events.Subscribe(Source, IgnoreCall);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _disposalSteps.Add($"DisposeAsyncCore saw {Source.SubscriberCount} subscribers");
        return ValueTask.CompletedTask;
    }

    private static void IgnoreCall(EventCall<string> call)
    {
    }
}
