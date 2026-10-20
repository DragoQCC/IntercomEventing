using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IntercomEventing.Generators.Tests.Infrastructure;

/// <summary>
/// Compiles test sources against the IntercomEventing library and runs <see cref="IntercomEventGenerator"/> over them with
/// <see cref="CSharpGeneratorDriver"/>.
/// </summary>
internal static class GeneratorHarness
{
    //~200 framework references are expensive to load, and every test needs the same set
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    /// <summary>
    /// The latest C# version this Roslyn knows, with documentation comments checked so a broken generated cref is a warning.
    /// </summary>
    public static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.Latest, DocumentationMode.Diagnose);

    /// <summary>
    /// A library compilation of <paramref name="sources"/> with nullable reference types enabled.
    /// </summary>
    public static CSharpCompilation CreateCompilation(params TestSource[] sources)
    {
        return CreateCompilation("GeneratorTestAssembly", [], sources);
    }

    /// <summary>
    /// A library compilation named <paramref name="assemblyName"/> that also references <paramref name="additionalReferences"/>.
    /// </summary>
    public static CSharpCompilation CreateCompilation(string assemblyName, IEnumerable<MetadataReference> additionalReferences,
        params TestSource[] sources)
    {
        var syntaxTrees = sources.Select(ParseSource).ToArray();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var references = References.Value.Concat(additionalReferences);
        return CSharpCompilation.Create(assemblyName, syntaxTrees, references, options);
    }

    /// <summary>
    /// Parses one test file with <see cref="ParseOptions"/>.
    /// </summary>
    public static SyntaxTree ParseSource(TestSource source)
    {
        return CSharpSyntaxTree.ParseText(source.Text, ParseOptions, source.Path);
    }

    /// <summary>
    /// A driver for <see cref="IntercomEventGenerator"/>; <paramref name="trackSteps"/> records each pipeline step's run reason.
    /// </summary>
    public static GeneratorDriver CreateDriver(bool trackSteps = false)
    {
        var generators = ImmutableArray.Create(new IntercomEventGenerator().AsSourceGenerator());
        var driverOptions = new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: trackSteps);
        return CSharpGeneratorDriver.Create(generators, additionalTexts: null, ParseOptions, optionsProvider: null, driverOptions);
    }

    /// <summary>
    /// Runs the generator over one file named <c>Producer.cs</c>.
    /// </summary>
    public static GeneratorRun Run(string source)
    {
        return Run(new TestSource("Producer.cs", source));
    }

    /// <summary>
    /// Runs the generator over <paramref name="sources"/> and compiles the result.
    /// </summary>
    public static GeneratorRun Run(params TestSource[] sources)
    {
        return Run(CreateCompilation(sources));
    }

    /// <summary>
    /// Runs the generator over an existing compilation and compiles the result.
    /// </summary>
    public static GeneratorRun Run(Compilation compilation)
    {
        var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var filteredDiagnostics);
        //throws when the generator failed, so no test can pass on a run that silently produced nothing
        _ = GetSingleResult(driver);
        return new GeneratorRun(driver.GetRunResult(), outputCompilation, filteredDiagnostics);
    }

    /// <summary>
    /// The generator's result from the driver's last run; fails the test when the generator threw.
    /// </summary>
    /// <remarks>
    /// A generator exception does not fail the build: the compiler reports warning CS8785 and drops every file the generator
    /// added, so each test must check for it rather than only count generated files.
    /// </remarks>
    public static GeneratorRunResult GetSingleResult(GeneratorDriver driver)
    {
        var result = driver.GetRunResult().Results.Single();
        if (result.Exception is not null)
        {
            throw new InvalidOperationException($"The generator threw, which drops all of its output: {result.Exception}", result.Exception);
        }
        return result;
    }

    /// <summary>
    /// Runs <see cref="IntercomEventBackingFieldAnalyzer"/> over <paramref name="compilation"/> and returns what it reported,
    /// including AD0001 if the analyzer threw.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> RunBackingFieldAnalyzerAsync(Compilation compilation)
    {
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new IntercomEventBackingFieldAnalyzer());
        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>
    /// Emits <paramref name="compilation"/> to an in-memory assembly, so a test compilation can reference it as metadata
    /// rather than as source.
    /// </summary>
    public static MetadataReference EmitToReference(Compilation compilation)
    {
        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        if (!emitResult.Success)
        {
            var errors = DiagnosticFormatting.Describe(emitResult.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"The referenced test assembly did not compile:\n{errors}");
        }
        return MetadataReference.CreateFromImage(assemblyStream.ToArray());
    }

    //the running framework's own assemblies plus the IntercomEventing library the generator targets
    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var frameworkAssemblies = trustedAssemblies.Where(assemblyPath => Path.GetDirectoryName(assemblyPath) == frameworkDirectory);
        var libraryAssembly = typeof(IntercomEventBase<>).Assembly.Location;
        return frameworkAssemblies
            .Append(libraryAssembly)
            .Select(assemblyPath => (MetadataReference)MetadataReference.CreateFromFile(assemblyPath))
            .ToImmutableArray();
    }
}

/// <summary>
/// One source file of a test compilation.
/// </summary>
/// <param name="Path">File path; generator diagnostics are placed back in their file by this path and the text at their span.</param>
/// <param name="Text">C# source.</param>
internal sealed record TestSource(string Path, string Text);

/// <summary>
/// The outcome of one generator run: what was generated, what was reported, and the compilation including the generated files.
/// </summary>
/// <param name="RunResult">The driver's run result.</param>
/// <param name="OutputCompilation">The input compilation plus the generated syntax trees.</param>
/// <param name="ReportedDiagnostics">Generator diagnostics after severity filtering. A diagnostic inside <c>#pragma warning disable</c>
/// stays in the list with <see cref="Diagnostic.IsSuppressed"/> set, as the compiler keeps it (and does not print it).</param>
internal sealed record GeneratorRun(GeneratorDriverRunResult RunResult, Compilation OutputCompilation, ImmutableArray<Diagnostic> ReportedDiagnostics)
{
    /// <summary>
    /// The files the generator added.
    /// </summary>
    public ImmutableArray<GeneratedSourceResult> GeneratedSources => RunResult.Results.Single().GeneratedSources;

    /// <summary>
    /// The text of the only generated file; fails the test when there is not exactly one.
    /// </summary>
    public string SingleGeneratedText
    {
        get
        {
            var generatedSources = GeneratedSources;
            if (generatedSources.Length != 1)
            {
                throw new InvalidOperationException($"Expected one generated file but found {generatedSources.Length}.");
            }
            return generatedSources[0].SourceText.ToString();
        }
    }

    /// <summary>
    /// Every compiler diagnostic (warnings included) inside a generated file, plus every error anywhere, formatted one per line.
    /// Empty when the generated code is clean.
    /// </summary>
    public string DescribeCompilationProblems()
    {
        var generatedTrees = RunResult.GeneratedTrees.ToHashSet();
        var problems = OutputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
            .Where(diagnostic =>
            {
                var isInGeneratedFile = diagnostic.Location.SourceTree is not null && generatedTrees.Contains(diagnostic.Location.SourceTree);
                var isError = diagnostic.Severity == DiagnosticSeverity.Error;
                return isInGeneratedFile || isError;
            });
        return DiagnosticFormatting.Describe(problems);
    }

    /// <summary>
    /// The generator's reported diagnostics, formatted one per line.
    /// </summary>
    public string DescribeReportedDiagnostics()
    {
        return DiagnosticFormatting.Describe(ReportedDiagnostics);
    }
}

/// <summary>
/// Turns diagnostics into readable text, so a failing assertion shows what was reported.
/// </summary>
internal static class DiagnosticFormatting
{
    public static string Describe(IEnumerable<Diagnostic> diagnostics)
    {
        return string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString()));
    }

    /// <summary>
    /// The source text the diagnostic points at, such as the field name it is reported on.
    /// </summary>
    public static string GetLocatedText(Diagnostic diagnostic)
    {
        var syntaxTree = diagnostic.Location.SourceTree;
        if (syntaxTree is null)
        {
            return "<no source location>";
        }
        return syntaxTree.GetText().ToString(diagnostic.Location.SourceSpan);
    }
}
