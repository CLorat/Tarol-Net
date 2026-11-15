using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Messaging.Generators.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public void Consumers_get_their_set_and_switch()
        => Snapshot.Match("consumers", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Consumers)));

    [Fact]
    public void The_generated_code_compiles_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Consumers).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void An_assembly_without_consumers_gets_nothing()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain)).GetRunResult().GeneratedTrees.ShouldBeEmpty();

    private static (GeneratorDriver Driver, CSharpCompilation Compilation) First()
    {
        var compilation = GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Consumers);
        return (GeneratorTestHost.Run(compilation), compilation);
    }

    [Fact]
    public void An_unrelated_file_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop; public sealed class Unrelated;", GeneratorTestHost.Parse, "Other.cs",
            cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_a_factory_body_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var edited = Sources.Consumers.Replace("=> new((Id<Order>)m.Order, m.Amount);", "=> new((Id<Order>)m.Order, m.Amount + 0);", StringComparison.Ordinal);
        var next = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(t => t.FilePath == "Source1.cs"),
            CSharpSyntaxTree.ParseText(edited, GeneratorTestHost.Parse, "Source1.cs", cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

public sealed class DiagnosticTests
{
    private static System.Collections.Immutable.ImmutableArray<Diagnostic> Run(string body)
        => GeneratorTestHost.Diagnose(GeneratorTestHost.Usings + "namespace Shop;\npublic sealed record PaidV1(Guid Order, decimal Amount);\n" + body);

    private static string Message(string body, string id)
        => Run(body).First(d => d.Id == id).GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    private const string Pay = " public Result Execute(Order o, ChangeContext c) => o.Pay(Amount); }";

    [Theory]
    [InlineData("public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);", null)]
    [InlineData("public Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);", "public static method")]
    [InlineData("public static Pay From(PaidV1 m, int extra) => new((Id<Order>)m.Order, m.Amount);", "the second parameter is the Message")]
    [InlineData("public static Pay From(int amount) => new(default, amount);", "the contract record is a class")]
    [InlineData("public static object From(PaidV1 m) => new Pay((Id<Order>)m.Order, m.Amount);", "returns its change, Pay")]
    public void TMSG001_the_factory_is_a_public_static_From(string factory, string? message)
    {
        var body = "public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order> { [Consumes(\"billing.paid\", 1)] " + factory + Pay;
        if (message is null)
            Run(body).Ids().ShouldNotContain("TMSG001");
        else
            Message(body, "TMSG001").ShouldContain(message);
    }

    [Fact]
    public void TMSG001_only_changes_consume_and_names_are_plain()
    {
        Message("public sealed record NotAChange(decimal Amount) { [Consumes(\"billing.paid\", 1)] public static NotAChange From(PaidV1 m) => new(m.Amount); }",
            "TMSG001").ShouldContain("is not a change");
        Message("public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order> { [Consumes(\"billing.paid\", 1, Name = \"pay.ment\")] public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);" + Pay,
            "TMSG001").ShouldContain("letters, digits, '-' and '_'");
    }

    [Fact]
    public void TMSG002_one_factory_per_contract_version()
    {
        var body = """
            public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order>
            {
                [Consumes("billing.paid", 1)] public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);
                [Consumes("billing.paid", 1)] public static Pay Again(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);
            """ + Pay;
        Message(body, "TMSG002").ShouldContain("two factories for billing.paid v1");
    }

    [Theory]
    [InlineData("\"paid\", 1", "<system>.<name>")]
    [InlineData("\"Billing.paid\", 1", "lowercase letters")]
    [InlineData("\"billing.Paid\", 1", "lowercase letters, digits, '-' and '.'")]
    [InlineData("\"billing.paid\", 0", "the version is 1 or more")]
    public void TMSG003_the_contract_is_system_dot_name_with_a_version(string arguments, string message)
        => Message("public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order> { [Consumes(" + arguments + ")] public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);" + Pay,
            "TMSG003").ShouldContain(message);

    [Fact]
    public void TMSG006_a_payload_missing_from_the_JSON_context_is_a_warning()
    {
        const string body = "public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order> { [Consumes(\"billing.paid\", 1)] public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);" + Pay;
        var missing = Run(body + "\n[JsonSerializable(typeof(string))] public sealed partial class ShopJson : JsonSerializerContext;").Single(d => d.Id == "TMSG006");
        missing.Severity.ShouldBe(DiagnosticSeverity.Warning);
        missing.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("Shop.PaidV1");
        Run(body + "\n[JsonSerializable(typeof(PaidV1))] public sealed partial class ShopJson : JsonSerializerContext;").Ids().ShouldNotContain("TMSG006");
        Run(body).Ids().ShouldNotContain("TMSG006");                                     // no context in this compilation: nothing to compare
    }

    [Fact]
    public void TMSG007_one_change_is_one_consumer_and_names_are_unique()
    {
        Message("""
            public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order>
            {
                [Consumes("billing.paid", 1, Name = "A")] public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);
                [Consumes("billing.paid", 2, Name = "B")] public static Pay Next(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);
            """ + Pay, "TMSG007").ShouldContain("name the consumer differently (A, B)");
        Message("""
            public sealed partial record Pay(Id<Order> Order, decimal Amount) : IChange<Order>
            { [Consumes("billing.paid", 1)] public static Pay From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);
            """ + Pay + """

            public sealed partial record Other(Id<Order> Order, decimal Amount) : IChange<Order>
            { [Consumes("billing.refunded", 1, Name = "Pay")] public static Other From(PaidV1 m) => new((Id<Order>)m.Order, m.Amount);
              public Result Execute(Order o, ChangeContext c) => o.Pay(-Amount); }
            """, "TMSG007").ShouldContain("'Pay' is used by Other and Pay");
    }
}
