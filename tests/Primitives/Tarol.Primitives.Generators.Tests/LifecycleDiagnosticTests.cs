namespace Tarol.Primitives.Generators.Tests;

/// <summary>The lifecycle diagnostics TPRM016–023 and the CS8524 suppressor TPRMSPR01.</summary>
public class LifecycleDiagnosticTests
{
    private const string Usings = "using System; using Tarol.Primitives; using Tarol.Results; using Tarol.StronglyTypedId;\n";

    // an aggregate that uses the enum, so TPRM022/023 have handlers to look at
    private const string Aggregate = """
        public partial class Job : AggregateRoot<Job>
        {
            public Status State { get; private set; }
            public sealed record Started : Event;
            public sealed record Finished : Event;
            private void On(Started e) { _ = e; State = Status.Running; }
            private void On(Finished e) { _ = e; State = Status.Done; }
        }
        """;

    // the snippets leave members unused (CS0169, …): only the Tarol diagnostics matter here
    private static string[] Ids(params string[] sources)
        => [.. GeneratorTestHost.Diagnose([.. sources.Select(static s => Usings + s)]).Ids().Where(static id => id.StartsWith("TPRM", StringComparison.Ordinal))];

    private static string[] WithEnum(string declaration) => Ids(declaration, Aggregate);

    [Fact]
    public void A_Valid_Lifecycle_Has_No_Diagnostic()
        => WithEnum("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done }").ShouldBeEmpty();

    [Theory]
    [InlineData("[Lifecycle] public enum Status { [To(Other.X)] New, [To(Done)] Running, Done } public enum Other { X }")]
    [InlineData("[Lifecycle] public enum Status { [To(3)] New, [To(Done)] Running, Done }")]
    [InlineData("[Lifecycle] public enum Status { [To(\"Running\")] New, [To(Done)] Running, Done }")]
    [InlineData("[Lifecycle] public enum Status { [To((Status)7)] New, [To(Done)] Running, Done }")]
    [InlineData("[Lifecycle] public enum Status { [To] New, [To(Done)] Running, Done }")]
    public void TPRM016_A_Target_That_Is_Not_A_State(string declaration)
        => WithEnum(declaration).ShouldBe(["TPRM016"]);

    [Fact]
    public void TPRM017_A_State_No_Edge_Reaches()
        => WithEnum("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done, [To(Done)] Orphan }").ShouldBe(["TPRM017", "TPRM022"]);

    [Fact]
    public void TPRM017_Follows_A_Named_Initial_State()
        => WithEnum("[Lifecycle(Initial = nameof(Running))] public enum Status { [To(Running)] New, [To(Done)] Running, Done }").ShouldBe(["TPRM017", "TPRM022"]);

    [Theory]
    [InlineData("[Lifecycle] public enum Status { [To(Running, Running)] New, [To(Done)] Running, Done }")]
    [InlineData("[Lifecycle] public enum Status { [To(Running)][To(Running)] New, [To(Done)] Running, Done }")]
    [InlineData("[Lifecycle] public enum Status { [To(Running)] New, [To(Running, Done)] Running, Done }")]
    public void TPRM018_A_Redundant_Edge(string declaration)
        => WithEnum(declaration).ShouldBe(["TPRM018"]);

    [Theory]
    [InlineData("[Lifecycle, Flags] public enum Status { [To(Running)] New = 1, [To(Done)] Running = 2, Done = 4 }")]
    [InlineData("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done, Finished = Done }")]
    [InlineData("[Lifecycle(Initial = \"Nope\")] public enum Status { [To(Running)] New, [To(Done)] Running, Done }")]
    public void TPRM019_An_Enum_That_Cannot_Be_A_Lifecycle(string declaration)
        => WithEnum(declaration).ShouldBe(["TPRM019"]);

    [Theory]
    [InlineData("public class Box<T> { [Lifecycle] public enum Status { [To(B)] A, B } }")]
    [InlineData("public class Box { [Lifecycle] private enum Status { [To(B)] A, B } }")]
    [InlineData("[Lifecycle] public enum Status { }")]
    public void TPRM019_Without_An_Aggregate(string declaration)
        => Ids(declaration).ShouldBe(["TPRM019"]);

    [Fact]
    public void An_Invalid_Lifecycle_Generates_No_Class_And_No_Step_Check()
    {
        var compilation = GeneratorTestHost.Compile(Usings + "[Lifecycle, Flags] public enum Status { [To(Running)] New = 1, [To(Done)] Running = 2, Done = 4 }", Usings + Aggregate);
        var sources = GeneratorTestHost.Run(compilation).GetRunResult().Results.Single().GeneratedSources;
        sources.Select(static s => s.HintName).ShouldBe(["Job.Aggregate.g.cs"]);
        sources[0].SourceText.ToString().ShouldNotContain("ForbiddenStep");
    }

    [Fact]
    public void TPRM020_To_Without_Lifecycle()
        => Ids("public enum Plain { [To(Two)] One, Two }").ShouldBe(["TPRM020"]);

    [Theory]
    [InlineData("[Transition<Status>(Status.New, Status.Done)] public sealed record Skipped : Job.Event;")]           // not an edge
    [InlineData("[Transition<Status>(Status.New, (Status)9)] public sealed record Skipped : Job.Event;")]             // not a member
    [InlineData("[Transition<Plain>(Plain.One, Plain.Two)] public sealed record Skipped : Job.Event; public enum Plain { One, Two }")] // no [Lifecycle]
    public void TPRM021_A_Transition_That_Is_Not_An_Edge(string transition)
        => Ids("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done }",
                Aggregate.Replace("public sealed record Started : Event;", "public sealed record Started : Event; public void Skip() { }", StringComparison.Ordinal)
                + "\npublic partial class Job { private void On(Skipped e) { _ = e; State = Status.Done; } }",
                transition)
            .ShouldBe(["TPRM021"]);

    [Fact]
    public void TPRM022_A_State_No_On_Handler_Assigns()
        => WithEnum("[Lifecycle] public enum Status { [To(Running)] New, [To(Done, Failed)] Running, Done, Failed }").ShouldBe(["TPRM022"]);

    [Fact]
    public void TPRM022_Is_Not_Reported_For_The_Initial_State_Or_A_Computed_Assignment()
        => Ids("[Lifecycle] public enum Status { [To(Running)] New, [To(Done, Failed)] Running, Done, Failed }",
                Aggregate.Replace("private void On(Finished e) { _ = e; State = Status.Done; }", "public sealed record Ended(Status To) : Event; private void On(Finished e) { _ = e; State = Status.Done; } private void On(Ended e) => State = e.To;", StringComparison.Ordinal))
            .ShouldBeEmpty();

    [Fact]
    public void TPRM022_Counts_A_Private_Helper_Called_From_On()
        => Ids("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done }",
                Aggregate.Replace("private void On(Finished e) { _ = e; State = Status.Done; }", "private void On(Finished e) { _ = e; Finish(); } private void Finish() => State = Status.Done;", StringComparison.Ordinal))
            .ShouldBeEmpty();

    [Fact]
    public void TPRM022_Is_Not_Reported_For_An_Enum_No_Aggregate_Uses()
        => Ids("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done }").ShouldBeEmpty();

    [Theory]
    [InlineData("[Transition<Status>(Status.Running, Status.Done)] public sealed record Finished : Event;", "State = Status.Done;", new string[0])]
    [InlineData("[Transition<Status>(Status.Running, Status.Done)] public sealed record Finished : Event;", "State = Status.Running;", new[] { "TPRM022", "TPRM023" })]
    [InlineData("[Transition<Status>(Status.Running, Status.Done)] public sealed record Finished : Event;", "Note = 1;", new[] { "TPRM022", "TPRM023" })]
    [InlineData("[Transition<Status>(Status.Running, Status.Done)] public sealed record Finished : Event;", "Finish(); } private void Finish() { State = Status.Done;", new string[0])]
    public void TPRM023_The_On_Handler_Of_A_Transition_Assigns_Its_To(string @event, string body, string[] expected)
        => Ids("[Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done }",
                Aggregate.Replace("public sealed record Finished : Event;", @event + " public int Note { get; private set; }", StringComparison.Ordinal)
                    .Replace("private void On(Finished e) { _ = e; State = Status.Done; }", "private void On(Finished e) { _ = e; " + body + " }", StringComparison.Ordinal))
            .ShouldBe(expected);

    [Fact]
    public void TPRM015_The_Lifecycle_Hook_Is_Plumbing()
        => Ids("""
            public partial class Job : AggregateRoot<Job>
            {
                public sealed record Started : Event;
                public int N { get; private set; }
                private void On(Started e) => N++;
                public void Bad(in StreamEvent s) { ApplyRaised(in s); ForbiddenStep(new LifecycleViolation("a", "b", "c", "d")); ApplyLive(in s); }
                protected override void ApplyLive(in StreamEvent stream) => ApplyRaised(in stream);
            }
            """).ShouldBe(["TPRM015", "TPRM015", "TPRM015", "TPRM015", "TPRM015"]);

    // ── TPRMSPR01 ──

    private const string Switches = """
        [Lifecycle] public enum Status { [To(Running)] New, [To(Done)] Running, Done }
        public enum Plain { A, B }
        public static class Labels
        {
            public static string Of(Status s) => s switch { Status.New => "new", Status.Running => "running", Status.Done => "done" };
            public static string Of(Plain p) => p switch { Plain.A => "a", Plain.B => "b" };
        }
        """;

    [Fact]
    public void TPRMSPR01_Suppresses_CS8524_For_Lifecycle_Enums_Only()
        => GeneratorTestHost.CompilerWarningsWithSuppressor(Usings + Switches).ShouldBe([("CS8524", true), ("CS8524", false)], ignoreOrder: true);

    [Fact]
    public void TPRMSPR01_Keeps_CS8509_For_A_Missing_State()
        => GeneratorTestHost.CompilerWarningsWithSuppressor(Usings + Switches.Replace(", Status.Done => \"done\"", "", StringComparison.Ordinal))
            .ShouldBe([("CS8509", false), ("CS8524", false)], ignoreOrder: true);
}
