namespace IntercomEventing.Benchmark.Examples;

/// <summary>
/// One runnable, self-checking example. It prints what happens and throws <see cref="ExampleCheckFailedException"/> when
/// the library did not behave as the example expects.
/// </summary>
internal interface IExample
{
    /// <summary>
    /// The name used on the command line (<c>examples &lt;name&gt;</c>); matches the example's folder.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// One line describing what the example shows.
    /// </summary>
    string Summary { get; }

    /// <summary>
    /// Runs the example to completion and disposes everything it created.
    /// </summary>
    Task RunAsync();
}
