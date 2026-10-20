using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.Dispatch;

public sealed record ImportCompleted(string FileName);

public sealed record ImageUploaded(string FileName);

public sealed record ReportRequested(string ReportName);

/// <summary>
/// Per-event dispatch options: <see cref="DispatchMode.Sequential"/> runs handlers in subscription order,
/// <see cref="EventingOptions.MaxConcurrentHandlers"/> limits a parallel raise, and
/// <see cref="EventingOptions.StartNextEventHandlerAfter"/> moves a slow handler to the background.
/// </summary>
internal sealed class DispatchExample : IExample
{
    private static readonly TimeSpan SimulatedWork = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan SlowHandlerTimeout = TimeSpan.FromMilliseconds(100);

    //generous upper bound for a second handler to start beside the first on a busy machine
    private static readonly TimeSpan OverlapWaitLimit = TimeSpan.FromSeconds(5);

    public string Name => "Dispatch";

    public string Summary => "DispatchMode, MaxConcurrentHandlers and StartNextEventHandlerAfter";

    public async Task RunAsync()
    {
        await ShowSequentialDispatchAsync();
        await ShowParallelLimitAsync();
        await ShowSlowHandlerMovesToTheBackgroundAsync();
    }

    private static async Task ShowSequentialDispatchAsync()
    {
        ExampleCheck.Step("DispatchMode.Sequential: each import step starts after the previous one finished");
        await using var importCompleted = new IntercomEvent<ImportCompleted>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        var timeline = new ConcurrentQueue<string>();
        foreach (var stepName in new[] { "validate", "index", "notify" })
        {
            importCompleted.Subscribe(async call =>
            {
                timeline.Enqueue($"{stepName} started");
                await Task.Delay(SimulatedWork, call.CancellationToken);
                timeline.Enqueue($"{stepName} finished");
            });
        }

        await importCompleted.RaiseAsync(new ImportCompleted("customers.csv"));
        Console.WriteLine($"Timeline: {string.Join(", ", timeline)}");

        string[] expectedTimeline =
        [
            "validate started", "validate finished",
            "index started", "index finished",
            "notify started", "notify finished",
        ];
        ExampleCheck.That(timeline.SequenceEqual(expectedTimeline), "handlers ran one at a time in subscription order");
    }

    private static async Task ShowParallelLimitAsync()
    {
        ExampleCheck.Step("DispatchMode.Parallel with MaxConcurrentHandlers = 2: six thumbnail handlers");
        await using var imageUploaded = new IntercomEvent<ImageUploaded>(new EventingOptions
        {
            DispatchMode = DispatchMode.Parallel,
            MaxConcurrentHandlers = 2,
            //no handler timeout: a waiting handler is never moved aside, so only a parallel start can open the gate below
            StartNextEventHandlerAfter = null,
        });
        var meter = new ConcurrencyMeter();
        //opens once two handlers run at the same time; the first handler waits for it, so the overlap is guaranteed rather
        //than left to timing, and a raise that ran its handlers one at a time would fail with a TimeoutException instead
        var twoHandlersOverlapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedHandlers = 0;
        for (var thumbnailSize = 1; thumbnailSize <= 6; thumbnailSize++)
        {
            imageUploaded.Subscribe(async call =>
            {
                using (meter.Enter())
                {
                    if (meter.Peak >= 2)
                    {
                        twoHandlersOverlapped.TrySetResult();
                    }
                    await twoHandlersOverlapped.Task.WaitAsync(OverlapWaitLimit, call.CancellationToken);
                    await Task.Delay(SimulatedWork, call.CancellationToken);
                }
                Interlocked.Increment(ref finishedHandlers);
            });
        }

        await imageUploaded.RaiseAsync(new ImageUploaded("holiday.jpg"));
        Console.WriteLine($"Peak concurrency: {meter.Peak}");

        ExampleCheck.That(finishedHandlers == 6, "every handler ran before the raise completed");
        ExampleCheck.That(meter.Peak == 2, "two handlers ran side by side, and never more than two");
    }

    private static async Task ShowSlowHandlerMovesToTheBackgroundAsync()
    {
        ExampleCheck.Step($"StartNextEventHandlerAfter = {SlowHandlerTimeout.TotalMilliseconds} ms: a slow report handler moves to the background");
        await using var reportRequested = new IntercomEvent<ReportRequested>(new EventingOptions
        {
            DispatchMode = DispatchMode.Sequential,
            StartNextEventHandlerAfter = SlowHandlerTimeout,
        });
        var releaseSlowHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowHandlerFinished = false;
        var fastHandlerRan = false;

        reportRequested.Subscribe(async call =>
        {
            //stands in for rendering a large report. It ignores call.CancellationToken on purpose: disposal cancels that
            //token, and this handler should finish its work rather than stop
            await releaseSlowHandler.Task;
            slowHandlerFinished = true;
        });
        reportRequested.Subscribe(call => fastHandlerRan = true);

        try
        {
            await reportRequested.RaiseAsync(new ReportRequested("quarterly"));
            ExampleCheck.That(fastHandlerRan, "the next handler started once the slow one exceeded the timeout");
            ExampleCheck.That(!slowHandlerFinished, "RaiseAsync returned while the slow handler kept running in the background");
        }
        finally
        {
            //released even when a check failed: disposal waits for the background handler, which would otherwise wait forever
            releaseSlowHandler.TrySetResult();
        }
        //disposal waits for handlers that were moved to the background
        await reportRequested.DisposeAsync();
        ExampleCheck.That(slowHandlerFinished, "DisposeAsync waited for the background handler");
    }
}
