namespace IntercomEventing.Benchmark.Examples.Threshold;

/// <summary>
/// Tracks a battery's charge and warns on every drain that leaves it at or below 20 percent.
/// </summary>
public sealed class BatteryMonitor : IAsyncDisposable
{
    private const int LowChargePercent = 20;
    private const int FullChargePercent = 100;

    /// <summary>
    /// A payload-less threshold event with a custom check: the value falls towards the threshold, so "reached" means
    /// <c>charge &lt;= limit</c>. <see cref="ThresholdRaiseMode.EveryTimeReached"/> (the default) raises on every drain
    /// that leaves the charge low.
    /// </summary>
    public ThresholdEvent<int> LowBattery { get; } = new(threshold: LowChargePercent, isThresholdReached: (charge, limit) => charge <= limit);

    public BatteryMonitor()
    {
        //the running value starts at zero, which is already "low"; start from a full battery instead
        LowBattery.Reset(startingValue: FullChargePercent);
    }

    public Task<bool> DrainAsync(int percent)
    {
        return LowBattery.IncrementAsync(-percent, caller: this);
    }

    public ValueTask DisposeAsync()
    {
        return LowBattery.DisposeAsync();
    }
}
