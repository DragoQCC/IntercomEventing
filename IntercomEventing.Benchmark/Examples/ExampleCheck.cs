namespace IntercomEventing.Benchmark.Examples;

/// <summary>
/// The examples' self-checks: each prints the expectation it verified, or stops the example when it does not hold.
/// </summary>
internal static class ExampleCheck
{
    /// <summary>
    /// Prints <paramref name="expectation"/> when <paramref name="condition"/> holds; otherwise fails the example.
    /// </summary>
    /// <exception cref="ExampleCheckFailedException"><paramref name="condition"/> is <see langword="false"/>.</exception>
    public static void That(bool condition, string expectation)
    {
        if (!condition)
        {
            throw new ExampleCheckFailedException(expectation);
        }
        Console.WriteLine($"  ok: {expectation}");
    }

    /// <summary>
    /// Prints a step heading so the output reads as a short story.
    /// </summary>
    public static void Step(string description)
    {
        Console.WriteLine($"-- {description}");
    }
}

/// <summary>
/// Thrown when an example's expectation does not hold.
/// </summary>
internal sealed class ExampleCheckFailedException : Exception
{
    public ExampleCheckFailedException(string expectation)
        : base($"expected: {expectation}")
    {
    }
}
