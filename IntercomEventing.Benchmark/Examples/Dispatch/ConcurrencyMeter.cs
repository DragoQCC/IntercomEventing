namespace IntercomEventing.Benchmark.Examples.Dispatch;

/// <summary>
/// Counts how many handlers run at the same time and remembers the peak.
/// </summary>
public sealed class ConcurrencyMeter
{
    private int _running;
    private int _peak;

    public int Peak => Volatile.Read(ref _peak);

    /// <summary>
    /// Marks one handler as running until the returned scope is disposed.
    /// </summary>
    public IDisposable Enter()
    {
        var runningNow = Interlocked.Increment(ref _running);
        UpdatePeak(runningNow);
        return new RunningScope(this);
    }

    private void UpdatePeak(int runningNow)
    {
        var observedPeak = Volatile.Read(ref _peak);
        while (runningNow > observedPeak)
        {
            var previousPeak = Interlocked.CompareExchange(ref _peak, runningNow, observedPeak);
            if (previousPeak == observedPeak)
            {
                return;
            }
            observedPeak = previousPeak;
        }
    }

    private sealed class RunningScope : IDisposable
    {
        private readonly ConcurrencyMeter _meter;

        public RunningScope(ConcurrencyMeter meter)
        {
            _meter = meter;
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _meter._running);
        }
    }
}
