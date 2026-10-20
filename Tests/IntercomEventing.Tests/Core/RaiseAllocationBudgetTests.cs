using IntercomEventing.Tests.TestSupport;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// What a raise with synchronous subscribers allocates, held to a budget so a per-handler cost cannot grow unnoticed.
/// </summary>
/// <remarks>
/// <para>The budgets are the bytes per raise measured on 64-bit .NET 11 in a Release build, raised from an empty
/// <see cref="ExecutionContext"/> (see <see cref="AllocationMeasurement"/>). With a handler timeout
/// (<see cref="EventingOptions.StartNextEventHandlerAfter"/>, one second by default) each handler costs about 96 bytes more:
/// its tracking scope and the two <see cref="AsyncLocal{T}"/> writes that make it current, which cost more when the raiser's
/// context already holds AsyncLocal values. That cost is documented on the option; a change that raises a budget must be
/// deliberate, and lowering a budget after an improvement keeps it honest.</para>
/// <para>Unoptimized (Debug) builds allocate a class state machine for every async method, so these cases are measured in
/// Release builds only, like the zero-subscriber cases.</para>
/// </remarks>
public class RaiseAllocationBudgetTests
{
    private const int MeasuredRaises = 20_000;

    [Test]
    [Arguments(1, DispatchMode.Parallel, true, 216)]
    [Arguments(1, DispatchMode.Parallel, false, 120)]
    [Arguments(4, DispatchMode.Parallel, true, 664)]
    [Arguments(4, DispatchMode.Parallel, false, 280)]
    [Arguments(4, DispatchMode.Sequential, true, 504)]
    [Arguments(4, DispatchMode.Sequential, false, 120)]
    public async Task RaiseWithSynchronousSubscribers_StaysWithinItsAllocationBudget(int subscriberCount, DispatchMode dispatchMode,
        bool withHandlerTimeout, int budgetInBytesPerRaise)
    {
        Skip.When(!AllocationMeasurement.IsLibraryOptimized(),
            "IntercomEventing is a Debug build here: unoptimized async methods allocate a class state machine, so allocation budgets are measured in Release builds.");
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = dispatchMode,
            StartNextEventHandlerAfter = withHandlerTimeout ? TimeSpan.FromSeconds(1) : null,
        });
        for (var subscriberNumber = 0; subscriberNumber < subscriberCount; subscriberNumber++)
        {
            evt.Subscribe(CompleteSynchronously);
        }

        var allocatedBytes = await AllocationMeasurement.MeasureAllocatedBytesAsync(() => evt.RaiseAsync(1), MeasuredRaises);
        var bytesPerRaise = (double)allocatedBytes / MeasuredRaises;

        await Assert.That(bytesPerRaise).IsLessThanOrEqualTo(budgetInBytesPerRaise);
    }

    private static Task CompleteSynchronously(EventCall<int> call)
    {
        return Task.CompletedTask;
    }
}
