namespace IntercomEventing.Benchmark.Examples;

/// <summary>
/// Runs the examples selected on the command line and reports which passed.
/// </summary>
internal static class ExampleRunner
{
    /// <summary>
    /// Runs every example when <paramref name="exampleNames"/> is empty, otherwise the named ones (case-insensitive) in catalog order.
    /// </summary>
    /// <returns>0 when every selected example passed; 1 when one failed or a name is unknown.</returns>
    public static async Task<int> RunAsync(string[] exampleNames)
    {
        var unknownNames = exampleNames.Where(name => ExampleCatalog.Find(name) is null).ToArray();
        if (unknownNames.Length > 0)
        {
            Console.WriteLine($"Unknown example: {string.Join(", ", unknownNames)}");
            Console.WriteLine($"Available: {string.Join(", ", ExampleCatalog.All.Select(example => example.Name))}");
            return 1;
        }

        //catalog order, so an example that changes process-wide defaults (DependencyInjection) always runs last
        var selectedExamples = exampleNames.Length == 0
            ? ExampleCatalog.All
            : ExampleCatalog.All.Where(example => exampleNames.Contains(example.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

        var failedExampleNames = new List<string>();
        foreach (var example in selectedExamples)
        {
            var passed = await RunOneAsync(example);
            if (!passed)
            {
                failedExampleNames.Add(example.Name);
            }
        }

        Console.WriteLine();
        var passedCount = selectedExamples.Count - failedExampleNames.Count;
        Console.WriteLine($"{passedCount} of {selectedExamples.Count} examples passed.");
        if (failedExampleNames.Count > 0)
        {
            Console.WriteLine($"Failed: {string.Join(", ", failedExampleNames)}");
            return 1;
        }
        return 0;
    }

    private static async Task<bool> RunOneAsync(IExample example)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {example.Name}: {example.Summary}");
        try
        {
            await example.RunAsync();
            Console.WriteLine($"=== {example.Name}: passed");
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"=== {example.Name}: FAILED, {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }
}
