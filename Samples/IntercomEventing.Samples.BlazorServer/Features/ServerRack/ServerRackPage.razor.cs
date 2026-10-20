using IntercomEventing.Samples.BlazorServer.Shared;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Samples.BlazorServer.Features.ServerRack;

/// <summary>
/// Subscribes through <see cref="IntercomEventing.Blazor.IntercomComponentBase.Events"/>: the handlers run on this tab's
/// renderer even when another tab raised the event, the page re-renders afterwards, and the subscriptions are removed when
/// the page is disposed (navigating away or closing the tab).
/// </summary>
public partial class ServerRackPage
{
    private const int MaxLogEntries = 12;

    private readonly List<PowerLogEntry> _powerLog = [];

    [Inject]
    private RackInventory Rack { get; set; } = default!;

    [Inject]
    private CircuitVisitor Visitor { get; set; } = default!;

    protected IReadOnlyList<PowerLogEntry> PowerLog => _powerLog;

    protected string? LastResult { get; private set; }

    protected override void OnInitialized()
    {
        foreach (var server in Rack.Servers)
        {
            //returns null while the page is prerendered; the interactive instance subscribes when it initializes
            Events.Subscribe(server.PowerChanged, OnPowerChanged);
        }
    }

    protected async Task SetPowerAsync(RackServer server, PowerState newState)
    {
        //completes once every subscribed tab has handled the change
        var changed = await server.SetPowerAsync(newState, Visitor.Name);
        LastResult = changed ? null : $"{server.Name} is already {newState}, so nothing was raised.";
    }

    protected static string CssClassFor(PowerState state)
    {
        return state.ToString().ToLowerInvariant();
    }

    private void OnPowerChanged(StateChangeEventCall<PowerState, PowerChangeRequest> call)
    {
        //the server passes itself as the caller when it raises, and the metadata carries it to every handler
        var serverName = call.Metadata.Caller is RackServer server ? server.Name : call.Metadata.EventName;
        var requestedBy = call.Payload.RequestedBy;
        var entry = new PowerLogEntry(call.Metadata.RaisedAt, serverName, call.OldState, call.NewState, requestedBy, requestedBy == Visitor.Name);
        _powerLog.Insert(0, entry);
        if (_powerLog.Count > MaxLogEntries)
        {
            _powerLog.RemoveAt(_powerLog.Count - 1);
        }
    }

    protected sealed record PowerLogEntry(DateTimeOffset RaisedAt, string ServerName, PowerState OldState, PowerState NewState, string RequestedBy, bool FromThisTab);
}
