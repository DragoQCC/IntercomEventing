using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Moves a handler that a timer tick or a debounce raise started into the background on a <see cref="FakeTimeProvider"/>.
/// </summary>
/// <remarks>
/// Those raises run on a thread-pool thread, and the dispatcher registers the handler's timeout on the fake clock there, right
/// after the handler returned its task. A test that saw the handler start can therefore advance the clock before the timeout
/// exists, and the timeout would then count from the advanced time. Advancing in steps until the condition holds avoids that
/// race without depending on timing.
/// </remarks>
/// <example>
/// <code>
/// var movedToTheBackground = await FakeClockStepping.AdvancePastHandlerTimeoutUntilAsync(clock, handlerTimeout,
///     () => timer.BackgroundWorkCount == 1);
/// </code>
/// </example>
internal static class FakeClockStepping
{
    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan ConditionCheckWindow = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Advances <paramref name="clock"/> by one handler timeout (plus the grace) at a time until <paramref name="condition"/> holds.
    /// </summary>
    /// <returns><see langword="true"/> once the condition held; <see langword="false"/> after <see cref="Eventually.DefaultTimeout"/>.</returns>
    public static async Task<bool> AdvancePastHandlerTimeoutUntilAsync(FakeTimeProvider clock, TimeSpan handlerTimeout, Func<bool> condition)
    {
        var step = handlerTimeout + DocumentedTimeoutGrace;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < Eventually.DefaultTimeout)
        {
            clock.Advance(step);
            var conditionHeld = await Eventually.IsTrueAsync(condition, ConditionCheckWindow);
            if (conditionHeld)
            {
                return true;
            }
        }
        return condition();
    }
}
