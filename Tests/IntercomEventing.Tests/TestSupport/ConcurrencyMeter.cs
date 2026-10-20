namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Counts how many handlers are running at once and remembers the highest count seen.
/// </summary>
/// <example>
/// <code>
/// evt.Subscribe(async call =>
/// {
///     meter.Enter();
///     await gate.Task;
///     meter.Exit();
/// });
/// </code>
/// </example>
internal sealed class ConcurrencyMeter
{
    private int _running;
    private int _peak;
    private int _entered;

    /// <summary>
    /// Handlers between <see cref="Enter"/> and <see cref="Exit"/> right now.
    /// </summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>
    /// The highest <see cref="Running"/> value seen.
    /// </summary>
    public int Peak => Volatile.Read(ref _peak);

    /// <summary>
    /// How many times <see cref="Enter"/> was called.
    /// </summary>
    public int Entered => Volatile.Read(ref _entered);

    public void Enter()
    {
        Interlocked.Increment(ref _entered);
        var nowRunning = Interlocked.Increment(ref _running);
        RaisePeakTo(nowRunning);
    }

    public void Exit()
    {
        Interlocked.Decrement(ref _running);
    }

    //compare-and-swap loop: several threads may report a new peak at the same time, and only a higher value may win
    private void RaisePeakTo(int candidate)
    {
        var currentPeak = Volatile.Read(ref _peak);
        while (candidate > currentPeak)
        {
            var peakBeforeSwap = Interlocked.CompareExchange(ref _peak, candidate, currentPeak);
            if (peakBeforeSwap == currentPeak)
            {
                return;
            }
            currentPeak = peakBeforeSwap;
        }
    }
}
