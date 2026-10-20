using System.Reflection;
using IntercomEventing.Generators.Tests.Infrastructure;
using Microsoft.CodeAnalysis;

namespace IntercomEventing.Generators.Tests;

/// <summary>
/// Every diagnostic the generator reports: its ID, severity, message and the source text it points at, and whether the
/// field is still generated.
/// </summary>
public sealed class DiagnosticTests
{
    [Test]
    public async Task NonPartialContainingType_ReportsIntev001AtTheField()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV001", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("Type 'Producer' must be declared partial so the members for [IntercomEvent] field '_changed' can be generated");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task NonPartialEnclosingType_ReportsIntev001ForThatType()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public class Outer
            {
                public partial class Inner
                {
                    [IntercomEvent]
                    private readonly IntercomEvent<int> _changed = new();
                }
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV001", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).Contains("Type 'Outer' must be declared partial");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public partial interface IProducer { [IntercomEvent] private static readonly IntercomEvent<int> s_changed = new(); }", "s_changed", "the interface 'IProducer'")]
    [Arguments("file partial class Hidden { [IntercomEvent] private readonly IntercomEvent<int> _changed = new(); }", "_changed", "the file-local type 'Hidden'")]
    [Arguments("file partial class Hidden { public partial class Nested { [IntercomEvent] private readonly IntercomEvent<int> _changed = new(); } }", "_changed", "the file-local type 'Hidden'")]
    [Arguments("public enum Color { [IntercomEvent] Red }", "Red", "the enum 'Color'")]
    public async Task UnsupportedContainingType_ReportsIntev002(string declaration, string fieldName, string expectedTypeDescription)
    {
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            {{declaration}}
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV002", DiagnosticSeverity.Error, locatedText: fieldName);
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"Field '{fieldName}' is declared in {expectedTypeDescription}, but [IntercomEvent] fields must be declared in a partial class, struct or record");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("List<int>", "List<int>", "which does not derive from IntercomEventBase<TCall>")]
    [Arguments("IIntercomEvent<EventCall<int>>", "IIntercomEvent<EventCall<int>>", "which does not derive from IntercomEventBase<TCall>")]
    [Arguments("object", "object", "which does not derive from IntercomEventBase<TCall>")]
    //the constraint does make TEvent an event kind, so the message must not claim otherwise
    [Arguments("TEvent", "TEvent",
        "which is a type parameter; declare the field with a class type that derives from IntercomEventBase<TCall>, even when the type parameter is constrained to one")]
    public async Task FieldTypeThatIsNoEventKind_ReportsIntev003(string fieldType, string expectedTypeName, string expectedReason)
    {
        var run = GeneratorHarness.Run($$"""
            using System.Collections.Generic;
            using IntercomEventing;

            namespace Sample;

            public partial class Producer<TEvent>
                where TEvent : IntercomEvent<int>
            {
                [IntercomEvent]
                private readonly {{fieldType}} _changed = null!;
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV003", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"Field '_changed' has type '{expectedTypeName}', {expectedReason}");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task NullableFieldType_ReportsIntev004()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int>? _changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV004", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("Field '_changed' is declared as nullable 'IntercomEvent<int>?'; remove the '?' and always assign an event to it");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FieldThatIsNotReadOnly_ReportsIntev005WarningAndIsStillGenerated()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private IntercomEvent<int> _changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV005", DiagnosticSeverity.Warning, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("Field '_changed' should be readonly: assigning a new event to it leaves every existing subscriber attached to the old event");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task WarningInsidePragmaDisable_IsSuppressed()
    {
        //the diagnostic is placed in the field's syntax tree, so #pragma and per-file severities apply to it
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
            #pragma warning disable INTEV005
                [IntercomEvent]
                private IntercomEvent<int> _changed = new();
            #pragma warning restore INTEV005
            }
            """);

        //the compiler keeps a pragma-suppressed diagnostic, marked as suppressed, and does not print it
        var suppressedWarning = run.ReportedDiagnostics.Single();
        await Assert.That(suppressedWarning.Id).IsEqualTo("INTEV005");
        await Assert.That(suppressedWarning.IsSuppressed).IsTrue();
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
    }

    [Test]
    public async Task WarningOutsidePragmaDisable_IsNotSuppressed()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
            #pragma warning disable INTEV005
            #pragma warning restore INTEV005
                [IntercomEvent]
                private IntercomEvent<int> _changed = new();
            }
            """);

        var warning = run.ReportedDiagnostics.Single();
        await Assert.That(warning.Id).IsEqualTo("INTEV005");
        await Assert.That(warning.IsSuppressed).IsFalse();
    }

    [Test]
    [Arguments("[IntercomEvent(Name = \"123\")]", "_changed", "\"123\"", "123")]
    [Arguments("[IntercomEvent(Name = \"class\")]", "_changed", "\"class\"", "class")]
    [Arguments("[IntercomEvent(Name = \"\")]", "_changed", "\"\"", "")]
    [Arguments("[IntercomEvent(Name = \"User Created\")]", "_changed", "\"User Created\"", "User Created")]
    public async Task InvalidNameOverride_ReportsIntev006AtTheNameArgument(string attribute, string fieldName, string locatedText, string invalidName)
    {
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                {{attribute}}
                private readonly IntercomEvent<int> {{fieldName}} = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV006", DiagnosticSeverity.Error, locatedText);
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"'{invalidName}' is not a valid event name for field '{fieldName}'; set IntercomEventAttribute.Name to a C# identifier that is not a keyword");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("_")]
    [Arguments("m_")]
    [Arguments("__")]
    [Arguments("_1st")]
    public async Task FieldNameThatDerivesNoValidName_ReportsIntev006AtTheField(string fieldName)
    {
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> {{fieldName}} = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV006", DiagnosticSeverity.Error, locatedText: fieldName);
        await Assert.That(diagnostic.GetMessage()).EndsWith("it is derived from the field name, so set IntercomEventAttribute.Name to choose a valid name");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EventSuffixThatWouldLeaveNoValidName_IsKept_AndTheNameReportedAsDerived()
    {
        //removing Event from 1Event would leave 1, which is no valid name either; the message names what the field derives
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _1Event = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV006", DiagnosticSeverity.Error, locatedText: "_1Event");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("'1Event' is not a valid event name for field '_1Event'; it is derived from the field name, so set IntercomEventAttribute.Name to choose a valid name");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public void Changed() { }", "Changed", "the member 'Producer.Changed()'")]
    [Arguments("public int ChangedEvent => 0;", "ChangedEvent", "the member 'Producer.ChangedEvent'")]
    [Arguments("public sealed class Changed { }", "Changed", "the member 'Producer.Changed'")]
    public async Task GeneratedNameTakenByDeclaredMember_ReportsIntev007(string conflictingMember, string generatedName, string expectedConflict)
    {
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                {{conflictingMember}}
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"The member '{generatedName}' generated for field '_changed' conflicts with {expectedConflict}; set IntercomEventAttribute.Name to choose another name");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FieldNamedLikeItsEvent_ReportsIntev007AgainstTheFieldItself()
    {
        //a public-style field name derives the same name, which the field already uses
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> Changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "Changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("The member 'Changed' generated for field 'Changed' conflicts with the member 'Producer.Changed'; set IntercomEventAttribute.Name to choose another name");
    }

    [Test]
    public async Task FieldNamedLikeItsViewProperty_ReportsIntev007AgainstTheFieldItself()
    {
        //ChangedEvent loses its Event suffix and derives Changed, whose view property ChangedEvent is the field's own name
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> ChangedEvent = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "ChangedEvent");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("The member 'ChangedEvent' generated for field 'ChangedEvent' conflicts with the member 'Producer.ChangedEvent'; set IntercomEventAttribute.Name to choose another name");
    }

    [Test]
    public async Task RecordPrimaryConstructorProperty_IsAConflict()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial record Producer(int Changed)
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).Contains("conflicts with the member 'Producer.Changed';");
    }

    [Test]
    public async Task GeneratedNameEqualToContainingTypeOrTypeParameter_ReportsIntev007()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Changed
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }

            public partial class Producer<ValueChanged>
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _valueChanged = new();
            }
            """);

        var messages = run.ReportedDiagnostics.Select(diagnostic => diagnostic.GetMessage()).OrderBy(message => message, StringComparer.Ordinal).ToArray();
        await Assert.That(run.ReportedDiagnostics.All(diagnostic => diagnostic.Id == "INTEV007")).IsTrue();
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "The member 'Changed' generated for field '_changed' conflicts with its containing type 'Changed'; set IntercomEventAttribute.Name to choose another name",
            "The member 'ValueChanged' generated for field '_valueChanged' conflicts with the type parameter 'ValueChanged' of 'Producer<ValueChanged>'; set IntercomEventAttribute.Name to choose another name",
        });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public event System.Action? Changed;", "the inherited member 'BaseProducer.Changed'")]
    [Arguments("protected int ChangedEvent => 0;", "the inherited member 'BaseProducer.ChangedEvent'")]
    public async Task GeneratedNameWouldHideInheritedMember_ReportsIntev007(string baseMember, string expectedConflict)
    {
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            public class BaseProducer
            {
                {{baseMember}}

                //private base members are not inherited, so they never conflict
                private int Unrelated => 0;
            }

            public partial class Producer : BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).Contains($"conflicts with {expectedConflict};");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task PrivateBaseMemberWithTheGeneratedName_IsNoConflict()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public class BaseProducer
            {
                private int Changed => 0;
            }

            public partial class Producer : BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """);

        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GeneratedNameWouldHideMemberGeneratedForBaseType_ReportsIntev007()
    {
        //the base type's generated members are not in the compilation the generator reads, so they are found through its fields
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }

            public partial class Producer : BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<string> _changed = new();
            }
            """);

        //both of the derived field's members would hide the base type's generated members
        var messages = run.ReportedDiagnostics.Select(diagnostic => diagnostic.GetMessage()).ToArray();
        var reportedLines = run.ReportedDiagnostics.Select(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line).Distinct().ToArray();
        await Assert.That(run.ReportedDiagnostics.All(diagnostic => diagnostic.Id == "INTEV007")).IsTrue();
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "The member 'Changed' generated for field '_changed' conflicts with the inherited member 'BaseProducer.Changed' generated for field '_changed'; set IntercomEventAttribute.Name to choose another name",
            "The member 'ChangedEvent' generated for field '_changed' conflicts with the inherited member 'BaseProducer.ChangedEvent' generated for field '_changed'; set IntercomEventAttribute.Name to choose another name",
        });
        //reported on the derived type's field (line 13), while the base type's field is generated normally
        await Assert.That(reportedLines).IsEquivalentTo(new[] { 13 });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
    }

    [Test]
    public async Task GeneratedNameWouldHideMemberGeneratedInAReferencedAssembly_ReportsIntev007()
    {
        //a base type from metadata already contains the members generated for it, so they are found like any inherited member
        var baseAssembly = GeneratorHarness.Run(GeneratorHarness.CreateCompilation("BaseAssembly", [], new TestSource("BaseProducer.cs", """
            using IntercomEventing;

            namespace Library;

            public partial class BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """)));
        var baseReference = GeneratorHarness.EmitToReference(baseAssembly.OutputCompilation);

        var run = GeneratorHarness.Run(GeneratorHarness.CreateCompilation("DerivedAssembly", [baseReference], new TestSource("Producer.cs", """
            using IntercomEventing;

            namespace Sample;

            public partial class Producer : Library.BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """)));

        var messages = run.ReportedDiagnostics.Select(diagnostic => diagnostic.GetMessage()).ToArray();
        await Assert.That(run.ReportedDiagnostics.All(diagnostic => diagnostic.Id == "INTEV007")).IsTrue();
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "The member 'Changed' generated for field '_changed' conflicts with the inherited member 'BaseProducer.Changed'; set IntercomEventAttribute.Name to choose another name",
            "The member 'ChangedEvent' generated for field '_changed' conflicts with the inherited member 'BaseProducer.ChangedEvent'; set IntercomEventAttribute.Name to choose another name",
        });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task NameOverrideTakenByObjectMember_ReportsIntev007AtTheNameArgument()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent(Name = "ToString")]
                private readonly IntercomEvent<int> _changed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "\"ToString\"");
        await Assert.That(diagnostic.GetMessage()).Contains("conflicts with the inherited member 'object.ToString()';");
    }

    [Test]
    public async Task TwoFieldsGeneratingTheSameMember_ReportIntev008OnBoth()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                [IntercomEvent(Name = "Changed")]
                private readonly IntercomEvent<string> _textChanged = new();
            }
            """);

        var diagnostics = run.ReportedDiagnostics.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start).ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(4);
        await Assert.That(diagnostics.All(diagnostic => diagnostic.Id == "INTEV008" && diagnostic.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(diagnostics[0].GetMessage()).IsEqualTo("Fields '_changed' and '_textChanged' both generate the member 'Changed'; set IntercomEventAttribute.Name on one of them");
        await Assert.That(diagnostics[1].GetMessage()).IsEqualTo("Fields '_changed' and '_textChanged' both generate the member 'ChangedEvent'; set IntercomEventAttribute.Name on one of them");
        await Assert.That(DiagnosticFormatting.GetLocatedText(diagnostics[0])).IsEqualTo("_changed");
        await Assert.That(DiagnosticFormatting.GetLocatedText(diagnostics[2])).IsEqualTo("\"Changed\"");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EventOfOneFieldEqualToViewPropertyOfAnother_ReportsIntev008()
    {
        //_user generates User and UserEvent; Name = "UserEvent" is used as written and generates UserEvent and UserEventEvent
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _user = new();

                [IntercomEvent(Name = "UserEvent")]
                private readonly IntercomEvent<int> _userAudit = new();
            }
            """);

        var messages = run.ReportedDiagnostics.Select(diagnostic => diagnostic.GetMessage()).ToArray();
        await Assert.That(run.ReportedDiagnostics.All(diagnostic => diagnostic.Id == "INTEV008")).IsTrue();
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "Fields '_user' and '_userAudit' both generate the member 'UserEvent'; set IntercomEventAttribute.Name on one of them",
            "Fields '_userAudit' and '_user' both generate the member 'UserEvent'; set IntercomEventAttribute.Name on one of them",
        });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FieldsThatDifferOnlyByTheEventSuffix_ReportIntev008ForBothMembers()
    {
        //_userJoined and _userJoinedEvent both derive UserJoined, so both generate UserJoined and UserJoinedEvent
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _userJoined = new();

                [IntercomEvent]
                private readonly IntercomEvent<int> _userJoinedEvent = new();
            }
            """);

        var messages = run.ReportedDiagnostics.Select(diagnostic => diagnostic.GetMessage()).ToArray();
        await Assert.That(run.ReportedDiagnostics.All(diagnostic => diagnostic.Id == "INTEV008")).IsTrue();
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "Fields '_userJoined' and '_userJoinedEvent' both generate the member 'UserJoined'; set IntercomEventAttribute.Name on one of them",
            "Fields '_userJoined' and '_userJoinedEvent' both generate the member 'UserJoinedEvent'; set IntercomEventAttribute.Name on one of them",
            "Fields '_userJoinedEvent' and '_userJoined' both generate the member 'UserJoined'; set IntercomEventAttribute.Name on one of them",
            "Fields '_userJoinedEvent' and '_userJoined' both generate the member 'UserJoinedEvent'; set IntercomEventAttribute.Name on one of them",
        });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public partial class Producer", "internal sealed record Payload(int Value);", "IntercomEvent<Payload>", "Payload", "Producer")]
    [Arguments("public partial class Producer", "internal sealed record Payload(int Value);", "IntercomEvent<System.Collections.Generic.List<Payload>>", "Payload", "Producer")]
    [Arguments("public partial class Producer", "internal sealed record Payload(int Value);", "StateChangeEvent<Payload>", "Payload", "Producer")]
    [Arguments("public partial class Producer", "internal sealed record Payload(int Value);", "IntercomEvent<Payload[]>", "Payload", "Producer")]
    [Arguments("public partial class Producer", "public sealed record Payload(int Value);", "IntercomEvent<Producer.Hidden>", "Producer.Hidden", "Producer")]
    [Arguments("internal partial class Producer", "public sealed record Payload(int Value);", "IntercomEvent<Producer.Hidden>", "Producer.Hidden", "Producer")]
    public async Task CallTypeLessAccessibleThanProducer_ReportsIntev009(string producerDeclaration, string payloadDeclaration, string fieldType,
        string expectedType, string expectedProducer)
    {
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            {{payloadDeclaration}}

            {{producerDeclaration}}
            {
                private sealed record Hidden(int Value);

                [IntercomEvent]
                private readonly {{fieldType}} _changed = null!;
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV009", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"The public members generated for field '_changed' expose '{expectedType}', which is less accessible than '{expectedProducer}'");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ProtectedPayloadInPublicDerivedProducer_ReportsIntev009()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public class BaseProducer
            {
                protected sealed record Progress(int Percent);
            }

            public partial class Producer : BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<Progress> _progressed = new();
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV009", DiagnosticSeverity.Error, locatedText: "_progressed");
        await Assert.That(diagnostic.GetMessage()).Contains("expose 'BaseProducer.Progress', which is less accessible than 'Producer'");
    }

    [Test]
    public async Task SeveralProblemsOnOneField_AreAllReported()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            internal sealed record Payload(int Value);

            public class Producer
            {
                [IntercomEvent]
                private IntercomEvent<Payload> _changed = new();
            }
            """);

        var reportedIds = run.ReportedDiagnostics.Select(diagnostic => diagnostic.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        await Assert.That(reportedIds).IsEquivalentTo(new[] { "INTEV001", "INTEV005", "INTEV009" });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("IntercomEvent<MissingPayload>")]
    [Arguments("MissingEvent")]
    [Arguments("BrokenEvent")]
    public async Task FieldTypeThatDoesNotBind_IsLeftToTheCompiler(string fieldType)
    {
        //the compiler already reports CS0246; a generator diagnostic or generated code would only add noise
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            public sealed class BrokenEvent : MissingBaseEvent
            {
            }

            public class Producer
            {
                [IntercomEvent]
                private readonly {{fieldType}} _changed = null!;
            }
            """);

        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("_item", "public int this[int index] => index;", "Item", "the indexer 'Producer.this[int]', whose metadata name is 'Item'")]
    [Arguments("_changed", "[System.Runtime.CompilerServices.IndexerName(\"Changed\")] public int this[int index] => index;", "Changed",
        "the indexer 'Producer.this[int]', whose metadata name is 'Changed'")]
    public async Task GeneratedNameTakenByAnIndexer_ReportsIntev007(string fieldName, string indexer, string generatedName, string expectedConflict)
    {
        //an indexer is not found by name lookup, but its metadata name clashes (CS0102 inside the generated file)
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> {{fieldName}} = new();

                {{indexer}}
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: fieldName);
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"The member '{generatedName}' generated for field '{fieldName}' conflicts with {expectedConflict}; set IntercomEventAttribute.Name to choose another name");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public void add_Changed(Func<EventCall<int>, Task> handler) { }", "Changed",
        "the method 'Producer.add_Changed(Func<EventCall<int>, Task>)', which has the name and parameters of its add accessor")]
    [Arguments("public void remove_Changed(Func<EventCall<int>, Task> handler) { }", "Changed",
        "the method 'Producer.remove_Changed(Func<EventCall<int>, Task>)', which has the name and parameters of its remove accessor")]
    [Arguments("public IIntercomEvent<EventCall<int>>? get_ChangedEvent() => null;", "ChangedEvent",
        "the method 'Producer.get_ChangedEvent()', which has the name and parameters of its get accessor")]
    [Arguments("public void add_Changed<T>(Func<EventCall<int>, Task> handler) { }", "Changed",
        "the method 'Producer.add_Changed<T>(Func<EventCall<int>, Task>)', which has the name and parameters of its add accessor")]
    public async Task MethodWithAnAccessorSignature_ReportsIntev007(string method, string generatedName, string expectedConflict)
    {
        //the accessors reserve these names for these parameter types, whatever the method's type parameters (CS0082
        //inside the generated file)
        var run = GeneratorHarness.Run($$"""
            using System;
            using System.Threading.Tasks;
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                {{method}}
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"The member '{generatedName}' generated for field '_changed' conflicts with {expectedConflict}; set IntercomEventAttribute.Name to choose another name");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public void add_Changed(int handler) { }")]
    [Arguments("public void remove_Changed(ref Func<EventCall<int>, Task> handler) { }")]
    [Arguments("public void add_Changed<T>(Func<EventCall<T>, Task> handler) { }")]
    [Arguments("public int get_ChangedEvent(int index) => index;")]
    public async Task MethodWithAnAccessorNameButOtherParameters_IsNoConflict(string method)
    {
        //overloads of the accessor names are legal C#, so they must not block generation
        var run = GeneratorHarness.Run($$"""
            using System;
            using System.Threading.Tasks;
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                {{method}}
            }
            """);

        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("IntercomEvent<int>", "public void set_ChangedEvent(IIntercomEvent<EventCall<int>> view) { }", "ChangedEvent",
        "the method 'Producer.set_ChangedEvent(IIntercomEvent<EventCall<int>>)', which has the name and parameters of its set accessor")]
    [Arguments("IntercomEvent<(int A, int B)>", "public void add_Changed(Func<EventCall<(int X, int Y)>, Task> handler) { }", "Changed",
        "the method 'Producer.add_Changed(Func<EventCall<(int X, int Y)>, Task>)', which has the name and parameters of its add accessor")]
    [Arguments("IntercomEvent<dynamic>", "public void remove_Changed(Func<EventCall<object>, Task> handler) { }", "Changed",
        "the method 'Producer.remove_Changed(Func<EventCall<object>, Task>)', which has the name and parameters of its remove accessor")]
    [Arguments("IntercomEvent<int>", "private int get_ChangedEvent;", "ChangedEvent",
        "the member 'Producer.get_ChangedEvent', which has the name of its get accessor")]
    [Arguments("IntercomEvent<int>", "public event Action? add_Changed;", "Changed",
        "the member 'Producer.add_Changed', which has the name of its add accessor")]
    public async Task AccessorNameTakenAsTheCompilerSeesIt_ReportsIntev007(string fieldType, string member, string generatedName,
        string expectedConflict)
    {
        //CS0082 compares parameters the way an identity conversion does (tuple names and dynamic do not count), a get-only
        //property still reserves set_, and a member of any other kind with an accessor name is CS0102
        var run = GeneratorHarness.Run($$"""
            using System;
            using System.Threading.Tasks;
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly {{fieldType}} _changed = new();

                {{member}}
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV007", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"The member '{generatedName}' generated for field '_changed' conflicts with {expectedConflict}; set IntercomEventAttribute.Name to choose another name");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SiblingWhoseGeneratedNameIsAnAccessorName_ReportsIntev007OnBothFields()
    {
        //_other's event get_ChangedEvent would take the get accessor of _changed's ChangedEvent property (CS0102)
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                [IntercomEvent(Name = "get_ChangedEvent")]
                private readonly IntercomEvent<int> _other = new();
            }
            """);

        var messages = run.ReportedDiagnostics.Select(diagnostic => diagnostic.GetMessage()).ToArray();
        await Assert.That(run.ReportedDiagnostics.All(diagnostic => diagnostic.Id == "INTEV007")).IsTrue().Because(run.DescribeReportedDiagnostics());
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "The member 'ChangedEvent' generated for field '_changed' conflicts with the member 'get_ChangedEvent' generated for field '_other', which has the name of its get accessor; set IntercomEventAttribute.Name to choose another name",
            "The member 'get_ChangedEvent' generated for field '_other' conflicts with the get accessor of the member 'ChangedEvent' generated for field '_changed'; set IntercomEventAttribute.Name to choose another name",
        });
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InheritedGenericMembersWithTheGeneratedNames_AreNoConflict()
    {
        //the compiler hides only members of the same name and arity, so Changed<T>() and ChangedEvent<T> stay visible
        //without CS0108; reporting INTEV007 here would block a valid program
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public class BaseProducer
            {
                public void Changed<T>()
                {
                }

                public class ChangedEvent<T>
                {
                }
            }

            public partial class Producer : BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }
            """);

        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CallTypeFromAReferenceWithOnlyAnExternAlias_ReportsIntev012()
    {
        //the generated file writes global::External.Payload, which an extern-aliased reference does not provide (CS0400)
        var payloadReference = CreatePayloadReference(aliases: ["Other"]);
        var run = GeneratorHarness.Run(GeneratorHarness.CreateCompilation("ProducerAssembly", [payloadReference], new TestSource("Producer.cs", """
            extern alias Other;

            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<Other::External.Payload> _changed = new();
            }
            """)));

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV012", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("The members generated for field '_changed' would expose 'Payload' from assembly 'PayloadAssembly', which is referenced only through the extern alias 'Other'; add 'global' to that reference's aliases, for example Aliases=\"global,Other\"");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task CallTypeFromAReferenceAliasedAsGlobalToo_IsGenerated()
    {
        var payloadReference = CreatePayloadReference(aliases: ["global", "Other"]);
        var run = GeneratorHarness.Run(GeneratorHarness.CreateCompilation("ProducerAssembly", [payloadReference], new TestSource("Producer.cs", """
            extern alias Other;

            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<Other::External.Payload> _changed = new();
            }
            """)));

        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(1);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("public static partial class Producer", "", "CS0708")]
    [Arguments("internal partial class Producer", "file sealed record Payload(int Value);", "CS9051")]
    public async Task FieldTheCompilerAlreadyRejects_IsLeftToTheCompiler(string producerDeclaration, string payloadDeclaration,
        string expectedCompilerError)
    {
        //an instance field in a static class, or a file-local payload in a member of a type that is not file-local, already
        //fails at the field; generated members would only repeat those errors inside the generated file
        var payloadType = payloadDeclaration.Length == 0 ? "int" : "Payload";
        var run = GeneratorHarness.Run($$"""
            using IntercomEventing;

            namespace Sample;

            {{payloadDeclaration}}

            {{producerDeclaration}}
            {
                [IntercomEvent]
                private readonly IntercomEvent<{{payloadType}}> _changed = new();
            }
            """);

        var compilerErrorIds = run.OutputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Id)
            .Distinct()
            .ToArray();
        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
        await Assert.That(compilerErrorIds).IsEquivalentTo(new[] { expectedCompilerError });
    }

    [Test]
    public async Task PrivatePayloadOfAPrivateNestedProducer_ReportsIntev009()
    {
        //Worker's public members are visible everywhere in Outer, but Progress only inside Worker
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Outer
            {
                private partial class Worker
                {
                    private sealed record Progress(int Percent);

                    [IntercomEvent]
                    private readonly IntercomEvent<Progress> _progressed = new();
                }
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV009", DiagnosticSeverity.Error, locatedText: "_progressed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("The public members generated for field '_progressed' expose 'Outer.Worker.Progress', which is less accessible than 'Outer.Worker'");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    //internal payloads
    [Arguments("public partial class Producer { MEMBER }", "InternalPayload")]
    [Arguments("internal partial class Producer { MEMBER }", "InternalPayload")]
    [Arguments("public partial class Producer { MEMBER }", "System.Collections.Generic.List<InternalPayload>")]
    [Arguments("public partial class Producer { private partial class Worker { MEMBER } }", "InternalPayload")]
    //private payloads
    [Arguments("public partial class Outer { private sealed record Secret; private partial class Worker { MEMBER } }", "Secret")]
    [Arguments("public partial class Outer { private partial class Worker { private sealed record Secret; MEMBER } }", "Secret")]
    [Arguments("public partial class Outer { private sealed record Secret; public partial class Worker { MEMBER } }", "Secret")]
    [Arguments("public partial class Outer { private sealed record Secret; protected partial class Worker { MEMBER } }", "Secret")]
    [Arguments("public partial class Outer { private sealed record Secret; private partial class Worker { private partial class Deeper { MEMBER } } }", "Secret")]
    //protected, protected internal and private protected payloads of Base
    [Arguments("public partial class Derived : Base { MEMBER }", "ProtectedPayload")]
    [Arguments("public partial class Derived : Base { protected partial class Worker { MEMBER } }", "ProtectedPayload")]
    [Arguments("public partial class Derived : Base { private partial class Worker { MEMBER } }", "ProtectedPayload")]
    [Arguments("public partial class Derived : Base { public partial class Mid { protected partial class Worker { MEMBER } } }", "ProtectedPayload")]
    [Arguments("public partial class Derived : Base { public partial class Mid { private partial class Worker { MEMBER } } }", "ProtectedPayload")]
    [Arguments("public partial class Derived : Base { MEMBER }", "ProtectedInternalPayload")]
    [Arguments("internal partial class Derived : Base { MEMBER }", "ProtectedInternalPayload")]
    [Arguments("public partial class Derived : Base { protected partial class Worker { MEMBER } }", "ProtectedInternalPayload")]
    [Arguments("public partial class Derived : Base { private protected partial class Worker { MEMBER } }", "PrivateProtectedPayload")]
    [Arguments("public partial class Derived : Base { protected partial class Worker { MEMBER } }", "PrivateProtectedPayload")]
    [Arguments("internal partial class Derived : Base { protected partial class Worker { MEMBER } }", "PrivateProtectedPayload")]
    public async Task AccessibilityCheck_AgreesWithTheCompiler(string producerDeclaration, string payloadType)
    {
        //the same public members written by hand show whether the compiler accepts them (CS0053 for the property, CS7025
        //for the event); INTEV009 must be reported exactly when it does not
        const string sharedDeclarations = """
            using System;
            using System.Threading.Tasks;
            using IntercomEventing;

            namespace Sample;

            internal sealed record InternalPayload;

            public class Base
            {
                protected sealed record ProtectedPayload;

                protected internal sealed record ProtectedInternalPayload;

                private protected sealed record PrivateProtectedPayload;
            }

            """;
        var handWrittenMembers = $"public event Func<EventCall<{payloadType}>, Task>? Changed {{ add {{ }} remove {{ }} }} public IIntercomEvent<EventCall<{payloadType}>> ChangedEvent => null!;";
        var handWrittenCompilation = GeneratorHarness.CreateCompilation(new TestSource("HandWritten.cs",
            sharedDeclarations + producerDeclaration.Replace("partial ", string.Empty).Replace("MEMBER", handWrittenMembers)));
        string[] inconsistentAccessibilityIds = ["CS0053", "CS7025"];
        var handWrittenErrors = handWrittenCompilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        var compilerRejects = handWrittenErrors.Any(error => inconsistentAccessibilityIds.Contains(error.Id));
        //any other error would mean the case itself is broken rather than its accessibility
        var unrelatedErrors = handWrittenErrors.Where(error => !inconsistentAccessibilityIds.Contains(error.Id));
        await Assert.That(DiagnosticFormatting.Describe(unrelatedErrors)).IsEqualTo(string.Empty);

        var attributedField = $"[IntercomEvent] private readonly IntercomEvent<{payloadType}> _changed = new();";
        var run = GeneratorHarness.Run(new TestSource("Producer.cs", sharedDeclarations + producerDeclaration.Replace("MEMBER", attributedField)));
        var generatorRejects = run.ReportedDiagnostics.Any(diagnostic => diagnostic.Id == "INTEV009");

        await Assert.That(generatorRejects).IsEqualTo(compilerRejects).Because(run.DescribeReportedDiagnostics());
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ConstField_ReportsIntev010InsteadOfIntev005()
    {
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private const IntercomEvent<int> _changed = null!;
            }
            """);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV010", DiagnosticSeverity.Error, locatedText: "_changed");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("Field '_changed' is const, so it can only hold null; declare it readonly and assign it an event");
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("public partial class Producer { [field: IntercomEvent] private IntercomEvent<int> Changed { get; } = new(); }", "the property 'Changed'")]
    [Arguments("public partial class Producer { [field: IntercomEvent] public event System.Action? Changed, Closed; }", "the event 'Changed, Closed'")]
    [Arguments("public partial record Producer([field: IntercomEvent] IntercomEvent<int> Changed);", "the parameter 'Changed'")]
    public async Task AttributeWithTheFieldTargetOnAnotherDeclaration_ReportsIntev011FromTheAnalyzer(string declaration, string ownerDescription)
    {
        var compilation = GeneratorHarness.CreateCompilation(new TestSource("Producer.cs", $$"""
            using IntercomEventing;

            namespace Sample;

            {{declaration}}
            """));

        //the generator cannot see a backing field's attributes, so it neither generates nor reports anything
        var run = GeneratorHarness.Run(compilation);
        var analyzerDiagnostics = await GeneratorHarness.RunBackingFieldAnalyzerAsync(compilation);

        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(0);
        await Assert.That(analyzerDiagnostics.Length).IsEqualTo(1).Because(DiagnosticFormatting.Describe(analyzerDiagnostics));
        var diagnostic = analyzerDiagnostics[0];
        await Assert.That(diagnostic.Id).IsEqualTo("INTEV011");
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(DiagnosticFormatting.GetLocatedText(diagnostic)).IsEqualTo("IntercomEvent");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo($"[IntercomEvent] cannot be applied with the field: target to {ownerDescription}; declare a readonly field and put the attribute on it");
    }

    [Test]
    public async Task AttributeOnADeclaredField_IsNotReportedByTheAnalyzer()
    {
        //an explicit field: target on a field declaration is the field itself, which the generator reads, and other
        //field-targeted attributes on a property are none of the analyzer's business
        var compilation = GeneratorHarness.CreateCompilation(new TestSource("Producer.cs", """
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                [field: IntercomEvent]
                private readonly IntercomEvent<int> _opened = new();

                [field: System.NonSerialized]
                private int Count { get; } = 0;
            }
            """));

        var analyzerDiagnostics = await GeneratorHarness.RunBackingFieldAnalyzerAsync(compilation);
        var run = GeneratorHarness.Run(compilation);

        await Assert.That(DiagnosticFormatting.Describe(analyzerDiagnostics)).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(2);
    }

    [Test]
    public async Task FieldsThatCannotGenerate_DoNotTakeTheNamesOfValidFields()
    {
        //a base or sibling field whose type is no event kind generates nothing, so its derived name is still free
        var run = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using IntercomEventing;

            namespace Sample;

            public partial class BaseProducer
            {
                [IntercomEvent]
                private readonly List<int> _changed = new();
            }

            public partial class Producer : BaseProducer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                [IntercomEvent]
                private readonly List<int> _opened = new();

                [IntercomEvent(Name = "Opened")]
                private readonly IntercomEvent<int> _realOpened = new();
            }
            """);

        var reportedIds = run.ReportedDiagnostics.Select(diagnostic => diagnostic.Id).ToArray();
        await Assert.That(reportedIds).IsEquivalentTo(new[] { "INTEV003", "INTEV003" }).Because(run.DescribeReportedDiagnostics());
        await Assert.That(run.GeneratedSources.Length).IsEqualTo(2);
        await Assert.That(run.DescribeCompilationProblems()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DuplicateFieldDeclaration_IsLeftToTheCompilerAndKeepsOtherFilesGenerated()
    {
        //CS0102 already reports the duplicate; generating both would add two files with one hint name, which fails the
        //whole run and drops the file for Other as well
        var run = GeneratorHarness.Run("""
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();

                [IntercomEvent]
                private readonly IntercomEvent<int> _changed = new();
            }

            public partial class Other
            {
                [IntercomEvent]
                private readonly IntercomEvent<int> _opened = new();
            }
            """);

        var compilerErrorIds = run.OutputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Id)
            .Distinct()
            .ToArray();
        await Assert.That(run.DescribeReportedDiagnostics()).IsEqualTo(string.Empty);
        await Assert.That(run.GeneratedSources.Single().HintName).StartsWith("Sample.Other._opened.");
        await Assert.That(compilerErrorIds).IsEquivalentTo(new[] { "CS0102" });
    }

    [Test]
    public async Task TreesSharingAPath_GetTheDiagnosticInTheTreeOfTheField()
    {
        //compilations built in memory often leave every path empty; the text at the span tells the trees apart
        var unrelated = new TestSource(string.Empty, """
            namespace Sample;

            public class LongUnrelatedFile
            {
                public int A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z;
            }
            """);
        var producer = new TestSource(string.Empty, """
            using IntercomEventing;

            namespace Sample;

            public partial class Producer
            {
                [IntercomEvent]
                private IntercomEvent<int> _changed = new();
            }
            """);

        var run = GeneratorHarness.Run(unrelated, producer);

        var diagnostic = await AssertSingleDiagnosticAsync(run, "INTEV005", DiagnosticSeverity.Warning, locatedText: "_changed");
        await Assert.That(diagnostic.Location.SourceTree!.ToString()).Contains("public partial class Producer");
    }

    [Test]
    public async Task EveryDescriptor_HasAStableIdAHelpfulDescriptionAndNoHelpLink()
    {
        var descriptors = typeof(IntercomEventDiagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(descriptorField => (DiagnosticDescriptor)descriptorField.GetValue(null)!)
            .ToArray();

        var ids = descriptors.Select(descriptor => descriptor.Id).ToArray();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(descriptors.Length);
        await Assert.That(ids).IsEquivalentTo(Enumerable.Range(1, 12).Select(number => $"INTEV{number:D3}"));
        foreach (var descriptor in descriptors)
        {
            await Assert.That(descriptor.Category).IsEqualTo("IntercomEventing");
            await Assert.That(descriptor.HelpLinkUri).IsEqualTo(string.Empty);
            await Assert.That(descriptor.Description.ToString()).EndsWith(".");
            await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
        }
    }

    //a separate assembly that declares External.Payload, referenced under the given extern aliases
    private static MetadataReference CreatePayloadReference(string[] aliases)
    {
        var payloadCompilation = GeneratorHarness.CreateCompilation("PayloadAssembly", [], new TestSource("Payload.cs", """
            namespace External;

            public sealed record Payload(int Value);
            """));
        return GeneratorHarness.EmitToReference(payloadCompilation).WithAliases(aliases);
    }

    //asserts the run reported exactly one diagnostic, with this ID and severity, located on locatedText in a source tree
    private static async Task<Diagnostic> AssertSingleDiagnosticAsync(GeneratorRun run, string expectedId, DiagnosticSeverity expectedSeverity,
        string locatedText)
    {
        var reportedDiagnostics = run.DescribeReportedDiagnostics();
        await Assert.That(run.ReportedDiagnostics.Length).IsEqualTo(1).Because(reportedDiagnostics);
        var diagnostic = run.ReportedDiagnostics[0];
        await Assert.That(diagnostic.Id).IsEqualTo(expectedId);
        await Assert.That(diagnostic.Severity).IsEqualTo(expectedSeverity);
        await Assert.That(diagnostic.Location.IsInSource).IsTrue();
        await Assert.That(DiagnosticFormatting.GetLocatedText(diagnostic)).IsEqualTo(locatedText);
        return diagnostic;
    }
}
