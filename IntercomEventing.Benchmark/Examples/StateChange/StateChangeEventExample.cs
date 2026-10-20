namespace IntercomEventing.Benchmark.Examples.StateChange;

/// <summary>
/// <c>StateChangeEvent</c>: holds a current state and raises only when it actually changes, with the old and new state
/// in the call.
/// </summary>
internal sealed class StateChangeEventExample : IExample
{
    public string Name => "StateChange";

    public string Summary => "StateChangeEvent: raise only when a state actually changes";

    public async Task RunAsync()
    {
        await using var webServer = new Server("web-01");
        await using var databaseServer = new Server("db-01");
        await using var auditLog = new PowerAuditLog();
        auditLog.Watch(webServer);
        auditLog.Watch(databaseServer);

        ExampleCheck.Step("Powering both servers on, then repeating the last state");
        await webServer.SetPowerAsync(PowerState.Starting, requestedBy: "deploy pipeline");
        await webServer.SetPowerAsync(PowerState.On, requestedBy: "deploy pipeline");
        await databaseServer.SetPowerAsync(PowerState.On, requestedBy: "on-call engineer");
        var repeatedStateRaised = await webServer.SetPowerAsync(PowerState.On, requestedBy: "deploy pipeline");

        ExampleCheck.That(!repeatedStateRaised, "setting the current state again returned false and raised nothing");
        ExampleCheck.That(auditLog.Entries.Count == 3, "three real transitions were recorded");
        var expectedFirstEntry = "web-01: Off -> Starting (requested by deploy pipeline)";
        ExampleCheck.That(auditLog.Entries.First() == expectedFirstEntry, "each call carries OldState, NewState and the payload");
        ExampleCheck.That(webServer.PowerChanged.CurrentState == PowerState.On, "CurrentState holds the latest state");
    }
}
