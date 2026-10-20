using IntercomEventing.Benchmark.Examples.Debounce;
using IntercomEventing.Benchmark.Examples.DependencyInjection;
using IntercomEventing.Benchmark.Examples.Dispatch;
using IntercomEventing.Benchmark.Examples.ErrorHandling;
using IntercomEventing.Benchmark.Examples.GeneratedEvents;
using IntercomEventing.Benchmark.Examples.Generic;
using IntercomEventing.Benchmark.Examples.StateChange;
using IntercomEventing.Benchmark.Examples.Subscriptions;
using IntercomEventing.Benchmark.Examples.Threshold;
using IntercomEventing.Benchmark.Examples.Timed;

namespace IntercomEventing.Benchmark.Examples;

/// <summary>
/// Every example, in the order <c>examples</c> runs them.
/// </summary>
internal static class ExampleCatalog
{
    /// <summary>
    /// The examples. DependencyInjection stays last: <c>AddIntercomEventing</c> sets the process-wide
    /// <see cref="EventingOptions.Default"/>, and the examples before it should run with the built-in defaults.
    /// </summary>
    public static IReadOnlyList<IExample> All { get; } =
    [
        new GenericEventExample(),
        new ThresholdEventExample(),
        new StateChangeEventExample(),
        new TimedEventExample(),
        new DebounceEventExample(),
        new GeneratedEventsExample(),
        new SubscriptionsExample(),
        new ErrorHandlingExample(),
        new DispatchExample(),
        new DependencyInjectionExample(),
    ];

    /// <summary>
    /// The example called <paramref name="name"/> (case-insensitive), or <see langword="null"/>.
    /// </summary>
    public static IExample? Find(string name)
    {
        return All.FirstOrDefault(example => string.Equals(example.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
