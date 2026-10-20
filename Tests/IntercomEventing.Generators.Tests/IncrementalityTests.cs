using System.Collections;
using System.Reflection;
using IntercomEventing.Generators.Models;
using IntercomEventing.Generators.Tests.Infrastructure;
using Microsoft.CodeAnalysis;

namespace IntercomEventing.Generators.Tests;

/// <summary>
/// Checks that the pipeline caches: an edit that does not change an <c>[IntercomEvent]</c> field leaves every tracked step
/// and every generated file cached (even when the edit moves the field), and the tracked pipeline values never hold symbols
/// or syntax.
/// </summary>
public sealed class IncrementalityTests
{
    private const string ProducerPath = "Producer.cs";

    private const string ProducerSource = """
        using IntercomEventing;

        namespace Sample;

        public sealed record UserCreated(string Name);

        public sealed partial class UserRegistry
        {
            [IntercomEvent]
            private readonly IntercomEvent<UserCreated> _userCreated = new();

            [IntercomEvent(Name = "Removed")]
            private static readonly IntercomEvent<UserCreated> s_userRemoved = new();
        }
        """;

    private static readonly string[] TrackedStepNames =
    [
        IntercomEventTrackingNames.FieldResults,
        IntercomEventTrackingNames.Diagnostics,
        IntercomEventTrackingNames.GeneratableFields,
    ];

    [Test]
    public async Task AddingAnUnrelatedFile_KeepsEveryStepAndFileCached()
    {
        var compilation = GeneratorHarness.CreateCompilation(new TestSource(ProducerPath, ProducerSource));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        var unrelatedFile = GeneratorHarness.ParseSource(new TestSource("Unrelated.cs", "namespace Sample; public sealed class Unrelated { }"));
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(unrelatedFile));

        var result = GeneratorHarness.GetSingleResult(driver);
        await Assert.That(DescribeStepsNotCached(result)).IsEqualTo(string.Empty);
        await Assert.That(DescribeOutputsNotCached(result)).IsEqualTo(string.Empty);
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(2);
    }

    [Test]
    public async Task EditingTheProducerFileElsewhere_KeepsEveryStepAndFileCached()
    {
        var compilation = GeneratorHarness.CreateCompilation(new TestSource(ProducerPath, ProducerSource));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        //a member added after the fields changes the file but not what the fields generate
        var editedSource = ProducerSource.TrimEnd().TrimEnd('}') + "    public int Count => 0;\n}\n";
        var editedCompilation = ReplaceProducerFile(compilation, editedSource);
        driver = driver.RunGenerators(editedCompilation);

        var result = GeneratorHarness.GetSingleResult(driver);
        await Assert.That(DescribeStepsNotCached(result)).IsEqualTo(string.Empty);
        await Assert.That(DescribeOutputsNotCached(result)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InsertingLinesAboveTheFields_KeepsEveryStepAndFileCached()
    {
        var compilation = GeneratorHarness.CreateCompilation(new TestSource(ProducerPath, ProducerSource));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        //moves every span and line in the file; a field model that stored a location would compare unequal here and
        //regenerate every file on each keystroke above a field
        var editedCompilation = ReplaceProducerFile(compilation, "// a comment above everything\n// and another one\n" + ProducerSource);
        driver = driver.RunGenerators(editedCompilation);

        var result = GeneratorHarness.GetSingleResult(driver);
        await Assert.That(DescribeStepsNotCached(result)).IsEqualTo(string.Empty);
        await Assert.That(DescribeOutputsNotCached(result)).IsEqualTo(string.Empty);
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(2);
    }

    [Test]
    public async Task InsertingLinesAboveAFieldWithAWarning_MovesTheWarningAndKeepsItsFileCached()
    {
        const string warnedSource = """
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private IntercomEvent<int> _changed = new();
            }
            """;
        var compilation = GeneratorHarness.CreateCompilation(new TestSource(ProducerPath, warnedSource));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);
        var lineBeforeEdit = GetLine(GeneratorHarness.GetSingleResult(driver).Diagnostics.Single());

        driver = driver.RunGenerators(ReplaceProducerFile(compilation, "// one\n// two\n" + warnedSource));

        //the warning's span is part of the field's result, so that step changes, but the model of the file does not
        var result = GeneratorHarness.GetSingleResult(driver);
        var warning = result.Diagnostics.Single();
        await Assert.That(warning.Id).IsEqualTo("INTEV005");
        await Assert.That(GetLine(warning)).IsEqualTo(lineBeforeEdit + 2);
        await Assert.That(DescribeStepsNotCached(result, IntercomEventTrackingNames.GeneratableFields)).IsEqualTo(string.Empty);
        await Assert.That(GetGeneratedFileOutputReasons(result)).IsEquivalentTo(new[] { IncrementalStepRunReason.Cached });
    }

    [Test]
    public async Task ChangingTheNameOverride_RegeneratesOnlyThatField()
    {
        var compilation = GeneratorHarness.CreateCompilation(new TestSource(ProducerPath, ProducerSource));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        var editedCompilation = ReplaceProducerFile(compilation, ProducerSource.Replace("Name = \"Removed\"", "Name = \"Deleted\""));
        driver = driver.RunGenerators(editedCompilation);

        var result = GeneratorHarness.GetSingleResult(driver);
        var generatableFieldReasons = result.TrackedSteps[IntercomEventTrackingNames.GeneratableFields]
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToArray();
        await Assert.That(generatableFieldReasons.Count(reason => reason == IncrementalStepRunReason.Modified)).IsEqualTo(1);
        await Assert.That(generatableFieldReasons.Count(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged)).IsEqualTo(1);
        await Assert.That(result.GeneratedSources.Any(source => source.SourceText.ToString().Contains("DeletedEvent =>"))).IsTrue();
    }

    [Test]
    public async Task TrackedPipelineValues_HoldNoSymbolsOrSyntax()
    {
        //only the tracked steps are inspected: the untracked Combine that pairs each diagnostic with the compilation (to
        //re-anchor it at report time) holds a Compilation by design, and it runs only when there is a diagnostic
        //invalid fields too, so the diagnostic values are inspected as well
        var sourceWithDiagnostics = ProducerSource + """

            public partial class Broken
            {
                [IntercomEvent]
                private System.Collections.Generic.List<int> _notAnEvent = new();
            }
            """;
        var compilation = GeneratorHarness.CreateCompilation(new TestSource(ProducerPath, sourceWithDiagnostics));
        var driver = GeneratorHarness.CreateDriver(trackSteps: true).RunGenerators(compilation);

        var result = GeneratorHarness.GetSingleResult(driver);
        var offendingValues = new List<string>();
        foreach (var stepName in TrackedStepNames)
        {
            foreach (var output in result.TrackedSteps[stepName].SelectMany(step => step.Outputs))
            {
                CollectRoslynObjects(output.Value, $"{stepName} output", offendingValues, depth: 0);
            }
        }
        await Assert.That(result.TrackedSteps[IntercomEventTrackingNames.Diagnostics].SelectMany(step => step.Outputs).Count()).IsGreaterThan(0);
        await Assert.That(string.Join("\n", offendingValues)).IsEqualTo(string.Empty);
    }

    private static Compilation ReplaceProducerFile(Compilation compilation, string newSource)
    {
        var producerTree = compilation.SyntaxTrees.Single(syntaxTree => syntaxTree.FilePath == ProducerPath);
        var editedTree = GeneratorHarness.ParseSource(new TestSource(ProducerPath, newSource));
        return compilation.ReplaceSyntaxTree(producerTree, editedTree);
    }

    private static int GetLine(Diagnostic diagnostic)
    {
        return diagnostic.Location.GetLineSpan().StartLinePosition.Line;
    }

    //every output of the named steps (all tracked steps when none are named) must be Cached or Unchanged (re-run, but
    //equal to the previous value)
    private static string DescribeStepsNotCached(GeneratorRunResult result, params string[] stepNames)
    {
        var problems = new List<string>();
        var stepNamesToCheck = stepNames.Length > 0 ? stepNames : TrackedStepNames;
        foreach (var stepName in stepNamesToCheck)
        {
            if (!result.TrackedSteps.TryGetValue(stepName, out var steps))
            {
                //a step with no input items this run (such as diagnostics for valid fields) is not recorded
                continue;
            }
            foreach (var output in steps.SelectMany(step => step.Outputs))
            {
                var stayedCached = output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged;
                if (!stayedCached)
                {
                    problems.Add($"{stepName}: {output.Reason}");
                }
            }
        }
        return string.Join("\n", problems);
    }

    //the run reasons of the source output that writes the generated files; the diagnostics output is the other source
    //output, told apart by its input, which is a diagnostic rather than a field model
    private static IncrementalStepRunReason[] GetGeneratedFileOutputReasons(GeneratorRunResult result)
    {
        return result.TrackedOutputSteps
            .SelectMany(outputStep => outputStep.Value)
            .Where(step => step.Inputs.Any(input => input.Source.Outputs[input.OutputIndex].Value is IntercomEventFieldModel))
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToArray();
    }

    private static string DescribeOutputsNotCached(GeneratorRunResult result)
    {
        var problems = result.TrackedOutputSteps
            .SelectMany(outputStep => outputStep.Value.SelectMany(step => step.Outputs).Select(output => (outputStep.Key, output.Reason)))
            .Where(output => output.Reason != IncrementalStepRunReason.Cached)
            .Select(output => $"{output.Key}: {output.Reason}");
        return string.Join("\n", problems);
    }

    //walks a pipeline value through its properties and collections and records any Roslyn object that roots a compilation or tree
    private static void CollectRoslynObjects(object? value, string path, List<string> offendingValues, int depth)
    {
        if (value is null || depth > 8)
        {
            return;
        }
        var holdsRoslynObject = value is ISymbol or SyntaxNode or SyntaxTree or SemanticModel or Compilation or Location;
        if (holdsRoslynObject)
        {
            offendingValues.Add($"{path}: {value.GetType().Name}");
            return;
        }
        var valueType = value.GetType();
        //values with nothing Roslyn-rooted inside: plain values, rule descriptors, and spans and line positions
        var isPrimitiveOrEnum = valueType.IsPrimitive || valueType.IsEnum;
        var isText = value is string;
        var isRuleDescriptor = value is DiagnosticDescriptor;
        var isTextPosition = valueType.Namespace == "Microsoft.CodeAnalysis.Text";
        var isLeafValue = isPrimitiveOrEnum || isText || isRuleDescriptor || isTextPosition;
        if (isLeafValue)
        {
            return;
        }
        if (value is IEnumerable items)
        {
            var index = 0;
            foreach (var item in items)
            {
                CollectRoslynObjects(item, $"{path}[{index}]", offendingValues, depth + 1);
                index++;
            }
            return;
        }
        if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(ValueTuple<,>))
        {
            CollectRoslynObjects(valueType.GetField("Item1")!.GetValue(value), $"{path}.Item1", offendingValues, depth + 1);
            CollectRoslynObjects(valueType.GetField("Item2")!.GetValue(value), $"{path}.Item2", offendingValues, depth + 1);
            return;
        }
        foreach (var property in valueType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var isIndexer = property.GetIndexParameters().Length > 0;
            if (isIndexer)
            {
                continue;
            }
            CollectRoslynObjects(property.GetValue(value), $"{path}.{property.Name}", offendingValues, depth + 1);
        }
    }
}
