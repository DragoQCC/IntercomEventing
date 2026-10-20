using IntercomEventing.Benchmark.Benchmarks;
using IntercomEventing.Benchmark.Examples;

namespace IntercomEventing.Benchmark;

/// <summary>
/// Entry point for the examples and the benchmarks.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>dotnet run -c Release -- examples</c> runs every example; <c>examples Threshold</c> runs one.</description></item>
/// <item><description><c>dotnet run -c Release -- benchmarks</c> runs every benchmark in process; any further arguments go to
/// BenchmarkDotNet, for example <c>benchmarks --job Dry</c> or <c>benchmarks --filter *Raise*</c>. See
/// <see cref="BenchmarkLauncher"/> for the defaults it adds.</description></item>
/// </list>
/// </remarks>
internal static class Program
{
    private const string ExamplesCommand = "examples";
    private const string BenchmarksCommand = "benchmarks";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0];
        var commandArguments = args[1..];
        if (string.Equals(command, ExamplesCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await ExampleRunner.RunAsync(commandArguments);
        }
        if (string.Equals(command, BenchmarksCommand, StringComparison.OrdinalIgnoreCase))
        {
            return BenchmarkLauncher.Run(commandArguments);
        }

        Console.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -c Release -- examples [name ...]           run every example, or the named ones");
        Console.WriteLine("  dotnet run -c Release -- benchmarks [BenchmarkDotNet]  run the benchmarks, e.g. --job Dry");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        foreach (var example in ExampleCatalog.All)
        {
            Console.WriteLine($"  {example.Name,-22}{example.Summary}");
        }
    }
}
