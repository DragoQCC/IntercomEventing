namespace IntercomEventing.Benchmark.Examples.Threshold;

/// <summary>
/// <c>ThresholdEvent</c>: an atomic running value that raises when it reaches a threshold, either once per crossing or on
/// every increment past it, with a custom comparison for values that fall.
/// </summary>
internal sealed class ThresholdEventExample : IExample
{
    public string Name => "Threshold";

    public string Summary => "ThresholdEvent: raise when a running value reaches a limit";

    public async Task RunAsync()
    {
        await RunLoginLockoutAsync();
        await RunBatteryWarningsAsync();
    }

    private static async Task RunLoginLockoutAsync()
    {
        await using var guard = new LoginGuard();
        var lockouts = new List<ThresholdEventCall<int, LoginAttempt>>();
        using var lockoutSubscription = guard.TooManyFailedLogins.Subscribe(call =>
        {
            lockouts.Add(call);
            Console.WriteLine($"{call.Payload.UserName} locked out from {call.Payload.IpAddress}: {call.PreviousValue} -> {call.CurrentValue} failed logins (limit {call.Threshold})");
        });

        ExampleCheck.Step("Five failed logins with OnlyWhenCrossed");
        var raisedPerAttempt = new List<bool>();
        for (var attemptNumber = 1; attemptNumber <= 5; attemptNumber++)
        {
            var raised = await guard.RecordFailedLoginAsync(new LoginAttempt("ada", $"10.0.0.{attemptNumber}"));
            raisedPerAttempt.Add(raised);
        }
        ExampleCheck.That(raisedPerAttempt.SequenceEqual([false, false, true, false, false]), "only the third failure (the crossing) raised");
        var raisedOnceWithTheCrossingAttempt = lockouts.Count == 1 && lockouts[0].Payload.IpAddress == "10.0.0.3";
        ExampleCheck.That(raisedOnceWithTheCrossingAttempt, "the call carries the payload of the increment that raised");
        ExampleCheck.That(guard.TooManyFailedLogins.CurrentValue == 5, "the value kept counting past the threshold");

        ExampleCheck.Step("A successful login resets the count, so the next three failures raise again");
        guard.RecordSuccessfulLogin();
        for (var attemptNumber = 1; attemptNumber <= 3; attemptNumber++)
        {
            await guard.RecordFailedLoginAsync(new LoginAttempt("ada", "10.0.1.1"));
        }
        ExampleCheck.That(lockouts.Count == 2, "the event raised again after Reset()");
    }

    private static async Task RunBatteryWarningsAsync()
    {
        await using var battery = new BatteryMonitor();
        var warnings = new List<int>();
        using var warningSubscription = battery.LowBattery.Subscribe(call =>
        {
            warnings.Add(call.CurrentValue);
            Console.WriteLine($"Low battery: {call.CurrentValue}% (was {call.PreviousValue}%)");
        });

        ExampleCheck.Step("Draining a battery that starts at 100% with a falling threshold of 20%");
        ExampleCheck.That(battery.LowBattery.CurrentValue == 100, "Reset(startingValue) replaced the initial value of zero");
        await battery.DrainAsync(50);
        await battery.DrainAsync(30);
        await battery.DrainAsync(5);
        ExampleCheck.That(warnings.SequenceEqual([20, 15]), "EveryTimeReached raised for each drain that left the charge at or below 20%");
    }
}
