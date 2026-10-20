using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.Components;

public partial class UserFeed
{
    private readonly List<string> _recentNames = [];

    [Inject]
    private UserDirectory Users { get; set; } = default!;

    [Inject]
    private RendererProbe Probe { get; set; } = default!;

    /// <summary>
    /// The subscription made in <see cref="OnInitialized"/>; <see langword="null"/> when nothing was subscribed (static rendering).
    /// </summary>
    public EventSubscription<EventCall<UserJoined>>? UserJoinedSubscription { get; private set; }

    protected IReadOnlyList<string> RecentNames => _recentNames;

    protected override void OnInitialized()
    {
        //runs on the renderer, re-renders afterwards, and is removed when the component is disposed
        UserJoinedSubscription = Events.Subscribe(Users.UserJoined, call =>
        {
            Probe.RecordHandlerCall();
            _recentNames.Insert(0, call.Payload.Name);
        });
    }
}
