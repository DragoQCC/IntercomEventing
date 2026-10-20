using BenchmarkDotNet.Running;

namespace IntercomEventing.Benchmark.Benchmarks;

/// <summary>
/// Runs the benchmarks through BenchmarkDotNet's command line, so every BenchmarkDotNet option works:
/// <c>--job Dry</c> for a quick smoke run, <c>--job Short</c> for rough numbers, <c>--filter *Subscription*</c> to pick benchmarks.
/// </summary>
/// <remarks>
/// Defaults the launcher adds when the caller does not pass the option:
/// <list type="bullet">
/// <item><description><c>--filter *</c>: without a filter BenchmarkDotNet asks interactively which benchmarks to run.</description></item>
/// <item><description><c>--inProcess</c>: BenchmarkDotNet 0.15.8 does not recognise the .NET 11 runtime, so its default
/// toolchain, which builds and starts a separate benchmark process, throws before running anything. Passing
/// <c>--inProcess</c>, <c>--runtimes</c> or <c>--coreRun</c> yourself replaces this default.</description></item>
/// <item><description><c>--artifacts</c> under the build output folder: BenchmarkDotNet otherwise writes its reports to
/// <c>BenchmarkDotNet.Artifacts</c> in the current directory, which is often the repository root.</description></item>
/// </list>
/// </remarks>
internal static class BenchmarkLauncher
{
    /// <returns>
    /// 0 when every selected benchmark ran, or when the caller only asked for information (<c>--help</c>, <c>--list</c>,
    /// <c>--info</c>, <c>--version</c>); 1 when nothing ran (for example a rejected option or a filter that matches nothing),
    /// BenchmarkDotNet reported a validation error, or a benchmark failed.
    /// </returns>
    public static int Run(string[] benchmarkDotNetArguments)
    {
        var arguments = AddDefaultArguments(benchmarkDotNetArguments);
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkLauncher).Assembly).Run(arguments);

        //BenchmarkDotNet prints why it ran nothing (bad option, empty filter) and returns no reports instead of throwing
        var anyBenchmarkRan = summaries.Any(summary => summary.Reports.Length > 0);
        if (!anyBenchmarkRan)
        {
            if (AskedOnlyForInformation(benchmarkDotNetArguments))
            {
                return 0;
            }
            Console.WriteLine("No benchmark ran; see BenchmarkDotNet's messages above.");
            return 1;
        }

        foreach (var summary in summaries)
        {
            var someBenchmarkFailed = summary.Reports.Any(report => !report.Success);
            if (summary.HasCriticalValidationErrors || someBenchmarkFailed)
            {
                return 1;
            }
        }
        return 0;
    }

    private static string[] AddDefaultArguments(string[] callerArguments)
    {
        var arguments = new List<string>(callerArguments);

        if (!HasOption(callerArguments, "filter", "f"))
        {
            arguments.AddRange(["--filter", "*"]);
        }

        var callerChoseHowToRun = HasOption(callerArguments, "inProcess", "i") ||
            HasOption(callerArguments, "runtimes", "r") ||
            HasOption(callerArguments, "coreRun");
        if (!callerChoseHowToRun)
        {
            arguments.Add("--inProcess");
        }

        if (!HasOption(callerArguments, "artifacts", "a"))
        {
            var artifactsFolder = Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts");
            arguments.AddRange(["--artifacts", artifactsFolder]);
        }

        return [.. arguments];
    }

    private static bool AskedOnlyForInformation(string[] callerArguments)
    {
        return HasOption(callerArguments, "help") ||
            HasOption(callerArguments, "list") ||
            HasOption(callerArguments, "info") ||
            HasOption(callerArguments, "version");
    }

    /// <summary>
    /// True when the caller passed the option as <c>--longName</c>, <c>--longName=value</c> or <c>-shortName</c>.
    /// </summary>
    private static bool HasOption(string[] callerArguments, string longName, string? shortName = null)
    {
        var longForm = $"--{longName}";
        var longFormWithValuePrefix = $"--{longName}=";
        var shortForm = shortName is null ? null : $"-{shortName}";
        foreach (var argument in callerArguments)
        {
            var isLongForm = string.Equals(argument, longForm, StringComparison.OrdinalIgnoreCase);
            var isLongFormWithValue = argument.StartsWith(longFormWithValuePrefix, StringComparison.OrdinalIgnoreCase);
            var isShortForm = shortForm is not null && string.Equals(argument, shortForm, StringComparison.OrdinalIgnoreCase);
            if (isLongForm || isLongFormWithValue || isShortForm)
            {
                return true;
            }
        }
        return false;
    }
}
