using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// Documents, as compiler output, the mistakes the 2.0 API turns into compile errors. Each snippet is compiled with Roslyn
/// against the built IntercomEventing assembly, and the test checks the exact set of errors, so an unrelated error (a broken
/// reference, a typo in the snippet) cannot make a test pass.
/// </summary>
/// <remarks>
/// <see cref="ValidUsage_Compiles"/> is the control: the same shared declarations and references compile cleanly.
/// </remarks>
public class MisuseCompileErrorTests
{
    //types every snippet uses; the snippet body is placed inside Snippet.Run(Producer producer)
    private const string SharedDeclarations = """
        using System;
        using System.Threading.Tasks;
        using IntercomEventing;

        public sealed record UserCreated(string Name);

        public sealed record OrderPlaced(int Number);

        public sealed class Producer
        {
            private readonly IntercomEvent<OrderPlaced> _orderPlaced = new();

            public IntercomEvent<UserCreated> UserCreated { get; } = new();

            public IIntercomEvent<EventCall<OrderPlaced>> OrderPlaced => _orderPlaced;
        }

        public static class Handlers
        {
            public static Task OnUserCreatedAsync(EventCall<UserCreated> call)
            {
                return Task.CompletedTask;
            }

            public static async void OnUserCreatedAsyncVoid(EventCall<UserCreated> call)
            {
                await Task.Yield();
            }
        }
        """;

    private static readonly Lazy<MetadataReference[]> s_compilationReferences = new(LoadCompilationReferences);

    [Test]
    public async Task ValidUsage_Compiles()
    {
        //S14 (namespace part): the shared declarations need nothing but `using IntercomEventing;`
        var errors = CompileErrorIds("""
            producer.UserCreated.Raised += Handlers.OnUserCreatedAsync;
            producer.UserCreated.Raised -= Handlers.OnUserCreatedAsync;
            producer.OrderPlaced.Raised += call => Task.CompletedTask;
            using var subscription = producer.UserCreated.Subscribe(call => Console.WriteLine(call.Payload.Name));
            _ = producer.UserCreated.RaiseAsync(new UserCreated("Ada"));
            """);

        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task PlusEqualsOnAGetOnlyEventProperty_IsCS0200()
    {
        //the event object has no += operator; += on the property itself is an assignment to a read-only property
        var errors = CompileErrorIds("producer.UserCreated += Handlers.OnUserCreatedAsync;");

        await Assert.That(errors).IsEquivalentTo(["CS0200"]);
    }

    [Test]
    public async Task PlusEqualsOnAnEventVariable_IsCS0019()
    {
        //on a local there is nothing to assign either: the event object defines no operator, Raised is the += member
        var errors = CompileErrorIds("""
            var userCreated = new IntercomEvent<UserCreated>();
            userCreated += Handlers.OnUserCreatedAsync;
            """);

        await Assert.That(errors).IsEquivalentTo(["CS0019"]);
    }

    [Test]
    public async Task RaiseThroughTheSubscribeOnlyView_IsCS1061()
    {
        var errors = CompileErrorIds("_ = producer.OrderPlaced.RaiseAsync(new OrderPlaced(1));");

        await Assert.That(errors).IsEquivalentTo(["CS1061"]);
    }

    [Test]
    public async Task TypoInsideARaisedLambda_IsReportedPreciselyAsCS1061()
    {
        //a user-defined += operator would have hidden this behind CS0019; the native event reports the real member error
        var errors = CompileErrorIds("producer.UserCreated.Raised += call => Console.Out.WriteLineAsync(call.Payload.Nmae);");

        await Assert.That(errors).IsEquivalentTo(["CS1061"]);
    }

    [Test]
    public async Task HandlerForTheWrongCallType_DoesNotCompile()
    {
        //S12: Subscribe<TWrongCall> compiled in 1.2.0 and failed only at raise time with an InvalidCastException
        var raisedErrors = CompileErrorIds("producer.UserCreated.Raised += (EventCall<OrderPlaced> call) => Task.CompletedTask;");
        var subscribeErrors = CompileErrorIds("_ = producer.UserCreated.Subscribe((EventCall<OrderPlaced> call) => Task.CompletedTask);");

        await Assert.That(raisedErrors).IsEquivalentTo(["CS1661", "CS1678"]);
        await Assert.That(subscribeErrors).IsEquivalentTo(["CS1661", "CS1678"]);
    }

    [Test]
    public async Task SynchronousLambdaOnRaised_DoesNotCompile()
    {
        //Raised takes Func<TCall, Task> only; synchronous handlers use Subscribe
        var errors = CompileErrorIds("producer.UserCreated.Raised += call => Console.WriteLine(call.Payload.Name);");

        await Assert.That(errors).IsEquivalentTo(["CS0029", "CS1662"]);
    }

    [Test]
    public async Task AsyncVoidMethodGroupOnRaised_IsCS0407()
    {
        var errors = CompileErrorIds("producer.UserCreated.Raised += Handlers.OnUserCreatedAsyncVoid;");

        await Assert.That(errors).IsEquivalentTo(["CS0407"]);
    }

    [Test]
    public async Task WithCopyOfAnEvent_IsCS8858()
    {
        //S6: 1.2.0 events were records, so a `with` copy shared the subscriber set, Id and lock with the original
        var errors = CompileErrorIds("var copy = producer.UserCreated with { };");

        await Assert.That(errors).IsEquivalentTo(["CS8858"]);
    }

    [Test]
    public async Task ChangingTheSharedCallInsideAHandler_DoesNotCompile()
    {
        //A10: in 1.2.0 a handler could overwrite EventCaller, and the next handler of the same raise saw the changed value
        var callerErrors = CompileErrorIds("""
            producer.UserCreated.Raised += call =>
            {
                call.Metadata.Caller = "overwritten by a handler";
                return Task.CompletedTask;
            };
            """);
        var metadataErrors = CompileErrorIds("""
            producer.UserCreated.Raised += call =>
            {
                call.Metadata = call.Metadata with { Caller = "overwritten by a handler" };
                return Task.CompletedTask;
            };
            """);
        var payloadErrors = CompileErrorIds("""
            producer.UserCreated.Raised += call =>
            {
                call.Payload = new UserCreated("Mallory");
                return Task.CompletedTask;
            };
            """);

        //EventMetadata is a positional record: its init-only Caller can only be set while building a new instance
        await Assert.That(callerErrors).IsEquivalentTo(["CS8852"]);
        await Assert.That(metadataErrors).IsEquivalentTo(["CS0200"]);
        await Assert.That(payloadErrors).IsEquivalentTo(["CS0200"]);
    }

    [Test]
    public async Task AsyncVoidMethodGroupOnSubscribe_CompilesSilently()
    {
        //documented hazard (decision D5): async void binds to Subscribe(Action<TCall>) and its exceptions bypass routing
        var errors = CompileErrorIds("_ = producer.UserCreated.Subscribe(Handlers.OnUserCreatedAsyncVoid);");

        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task SynchronousLambdaThatDropsATask_CompilesSilently()
    {
        //documented hazard (decision D5): a block body that calls an async method without returning it drops the task
        var errors = CompileErrorIds("""
            _ = producer.UserCreated.Subscribe(call =>
            {
                Handlers.OnUserCreatedAsync(call);
            });
            """);

        await Assert.That(errors).IsEmpty();
    }

    //compiles SharedDeclarations plus the snippet as a library and returns the distinct error ids, sorted
    private static string[] CompileErrorIds(string snippetBody)
    {
        var source = $$"""
            {{SharedDeclarations}}

            public static class Snippet
            {
                public static void Run(Producer producer)
                {
            {{snippetBody}}
                }
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var compilation = CSharpCompilation.Create("MisuseSnippet", [syntaxTree], s_compilationReferences.Value, compilationOptions);
        return compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Id)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    //the running shared framework (the trusted platform assemblies in the runtime directory) plus the built IntercomEventing.dll
    private static MetadataReference[] LoadCompilationReferences()
    {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Func<string, bool> isInTheRuntimeDirectory = assemblyPath =>
            string.Equals(Path.GetDirectoryName(assemblyPath), runtimeDirectory, StringComparison.OrdinalIgnoreCase);
        var frameworkAssemblies = trustedPlatformAssemblies.Where(isInTheRuntimeDirectory);
        var intercomEventingAssembly = typeof(IntercomEventBase<>).Assembly.Location;
        return frameworkAssemblies
            .Append(intercomEventingAssembly)
            .Select(assemblyPath => (MetadataReference)MetadataReference.CreateFromFile(assemblyPath))
            .ToArray();
    }
}
