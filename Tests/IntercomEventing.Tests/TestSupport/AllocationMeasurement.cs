using System.Diagnostics;
using System.Reflection;

namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Measures what synchronous raises allocate with <see cref="GC.GetAllocatedBytesForCurrentThread"/>: after a warm-up, in up
/// to <see cref="MeasurementWindows"/> windows, keeping the fewest bytes of any window.
/// </summary>
/// <remarks>
/// <para>A window can catch a rare one-off allocation on the test thread that no raise caused (one Release run in a few dozen
/// failed that way). A raise that allocates does so in every window, so the fewest bytes of a few windows still exposes it.</para>
/// <para>The raises run on a thread-pool thread with an empty <see cref="ExecutionContext"/>. Writing an
/// <see cref="AsyncLocal{T}"/> copies the context's whole value map, so the same raise allocates more when the caller already
/// carries AsyncLocal values, as the test framework's own context does. A clean context measures what the library itself
/// allocates.</para>
/// </remarks>
internal static class AllocationMeasurement
{
    private const int WarmUpRaises = 2_000;
    private const int MeasurementWindows = 3;

    /// <summary>
    /// Whether IntercomEventing was compiled with optimizations. An unoptimized (Debug) build gives every async method a class
    /// state machine, so allocation results are meaningful only in Release builds.
    /// </summary>
    public static bool IsLibraryOptimized()
    {
        //the compiler marks unoptimized assemblies with DebuggableAttribute(DisableOptimizations)
        var debuggable = typeof(IntercomEventBase<>).Assembly.GetCustomAttribute<DebuggableAttribute>();
        return debuggable is null || !debuggable.IsJITOptimizerDisabled;
    }

    /// <summary>
    /// The fewest bytes <paramref name="measuredRaises"/> calls of <paramref name="raise"/> allocated in any measurement window,
    /// measured in an empty <see cref="ExecutionContext"/>; stops at the first window that allocated nothing. Every raise must
    /// complete synchronously.
    /// </summary>
    /// <exception cref="InvalidOperationException">A warm-up raise did not complete synchronously.</exception>
    public static async Task<long> MeasureAllocatedBytesAsync(Func<Task> raise, int measuredRaises)
    {
        Task<long> measurement;
        //with the flow suppressed, the work item starts with an empty context instead of the test's
        using (ExecutionContext.SuppressFlow())
        {
            measurement = Task.Run(() => MeasureOnThisThread(raise, measuredRaises));
        }
        return await measurement;
    }

    private static long MeasureOnThisThread(Func<Task> raise, int measuredRaises)
    {
        for (var warmUp = 0; warmUp < WarmUpRaises; warmUp++)
        {
            var warmUpRaise = raise();
            if (!warmUpRaise.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The measured raise must complete synchronously.");
            }
        }
        var fewestAllocatedBytes = long.MaxValue;
        for (var window = 0; window < MeasurementWindows; window++)
        {
            var allocatedBytes = MeasureOneWindow(raise, measuredRaises);
            fewestAllocatedBytes = Math.Min(fewestAllocatedBytes, allocatedBytes);
            if (fewestAllocatedBytes == 0)
            {
                break;
            }
        }
        return fewestAllocatedBytes;
    }

    //the loop between the two readings runs on this thread only
    private static long MeasureOneWindow(Func<Task> raise, int measuredRaises)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < measuredRaises; index++)
        {
            _ = raise();
        }
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        return allocatedAfter - allocatedBefore;
    }
}
