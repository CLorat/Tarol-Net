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

public sealed class PublicationGeneratorTests
{
    [Fact]
    public void Publications_get_their_set_table_and_map()
        => Snapshot.Match("publications", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Publications)));

    [Fact]
    public void The_generated_publications_compile_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Publications).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void Consumers_and_publications_of_one_assembly_are_two_files()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Consumers, Sources.Publications.Replace(GeneratorTestHost.Usings, "using System; using Tarol.Messaging; using Tarol.Primitives; using Tarol.Results;\n", StringComparison.Ordinal)
                .Replace("public sealed record MoneyV2(decimal Amount, string Currency);", "", StringComparison.Ordinal)))
            .GetRunResult().GeneratedTrees.Select(t => Path.GetFileName(t.FilePath)).Order(StringComparer.Ordinal).ShouldBe(["GeneratedConsumers.g.cs", "GeneratedPublications.g.cs"]);

    private static (GeneratorDriver Driver, CSharpCompilation Compilation) First()
    {
        var compilation = GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Publications);
        return (GeneratorTestHost.Run(compilation), compilation);
    }

    [Fact]
    public void An_unrelated_file_regenerates_no_publication()
    {
        var (driver, compilation) = First();
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop; public sealed class Unrelated;", GeneratorTestHost.Parse, "Other.cs",
            cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Editing_a_mapping_body_regenerates_no_publication()
    {
        var (driver, compilation) = First();
        var edited = Sources.Publications.Replace("e.Amount > 0 ?", "e.Amount > 1 ?", StringComparison.Ordinal);
        var next = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(t => t.FilePath == "Source1.cs"),
            CSharpSyntaxTree.ParseText(edited, GeneratorTestHost.Parse, "Source1.cs", cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

public sealed class PublicationDiagnosticTests
{
    private const string Records = "namespace Shop;\npublic sealed record PaidV1(Guid Order, decimal Amount);\n";

    private static System.Collections.Immutable.ImmutableArray<Diagnostic> Run(string body)
        => GeneratorTestHost.Diagnose(GeneratorTestHost.Usings + Records + body);

    private static string Message(string body, string id)
        => Run(body).First(d => d.Id == id).GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("public static PaidV1 Map(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount);", null)]
    [InlineData("internal static PaidV1? Map(Order.PaymentRecorded e, RecordedEvent r) => null;", null)]
    [InlineData("public PaidV1 Map(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount);", "public or internal static method")]
    [InlineData("private static PaidV1 Map(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount);", "public or internal static method")]
    [InlineData("public static PaidV1 Map(Order.PaymentRecorded e, int extra) => new(Guid.Empty, e.Amount);", "the second parameter is the RecordedEvent")]
    [InlineData("public static PaidV1 Map(Order.Opened e) => new(Guid.Empty, 0);", "the first parameter is the event, PaymentRecorded")]
    [InlineData("public static decimal Map(Order.PaymentRecorded e) => e.Amount;", "returns the contract record, a class")]
    [InlineData("public static string Map(Order.PaymentRecorded e) => \"\";", "returns the contract record, a class")]
    [InlineData("public static void Map(Order.PaymentRecorded e) { }", "returns the contract record, a class")]
    public void TMSG004_the_publication_is_a_static_mapping_method(string method, string? message)
    {
        var body = "public static class Publications { [Publishes<Order.PaymentRecorded>(\"shop.paid\", 1)] " + method + " }";
        if (message is null)
            Run(body).Ids().ShouldNotContain("TMSG004");
        else
            Message(body, "TMSG004").ShouldContain(message);
    }

    [Fact]
    public void TMSG004_the_containing_type_is_visible()
        => Message("public static class Outer { private static class Publications { [Publishes<Order.PaymentRecorded>(\"shop.paid\", 1)] public static PaidV1 Map(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount); } }",
            "TMSG004").ShouldContain("of a visible type");

    [Theory]
    [InlineData("\"paid\", 1", "<system>.<name>")]
    [InlineData("\"shop.paid\", 0", "the version is 1 or more")]
    public void TMSG003_applies_to_publications(string arguments, string message)
    {
        var text = Message("public static class Publications { [Publishes<Order.PaymentRecorded>(" + arguments + ")] public static PaidV1 Map(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount); }", "TMSG003");
        text.ShouldContain("[Publishes<PaymentRecorded>(");
        text.ShouldContain(message);
    }

    [Fact]
    public void TMSG005_one_method_per_contract_version_is_an_error()
    {
        var found = Run("""
            public static class Publications
            {
                [Publishes<Order.PaymentRecorded>("shop.paid", 1)] public static PaidV1 A(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount);
                [Publishes<Order.Opened>("shop.paid", 1)] public static PaidV1 B(Order.Opened e) => new(Guid.Empty, 0);
            }
            """).Where(d => d.Id == "TMSG005").ShouldHaveSingleItem();
        found.Severity.ShouldBe(DiagnosticSeverity.Error);
        found.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("shop.paid v1 is published by two methods, 'Publications.A' and 'Publications.B'");
    }

    [Fact]
    public void TMSG005_two_prefixes_in_one_assembly_is_a_warning()
    {
        var diagnostics = Run("""
            public static class Publications
            {
                [Publishes<Order.PaymentRecorded>("shop.paid", 1)] public static PaidV1 A(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount);
                [Publishes<Order.Opened>("billing.opened", 1)] public static PaidV1 B(Order.Opened e) => new(Guid.Empty, 0);
            }
            """);
        var found = diagnostics.Where(d => d.Id == "TMSG005").ShouldHaveSingleItem();
        found.Severity.ShouldBe(DiagnosticSeverity.Warning);
        found.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("2 system prefixes (billing, shop)");
        diagnostics.Ids().ShouldBe(["TMSG005"]);                                            // the set is still generated and compiles
    }

    [Fact]
    public void TMSG006_a_contract_record_missing_from_the_JSON_context_is_a_warning()
    {
        const string body = "public static class Publications { [Publishes<Order.PaymentRecorded>(\"shop.paid\", 1)] public static PaidV1 Map(Order.PaymentRecorded e) => new(Guid.Empty, e.Amount); }";
        var missing = Run(body + "\n[JsonSerializable(typeof(string))] public sealed partial class ShopJson : JsonSerializerContext;").Single(d => d.Id == "TMSG006");
        missing.Severity.ShouldBe(DiagnosticSeverity.Warning);
        missing.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("'Shop.PaidV1' (the record of shop.paid v1)");
        Run(body + "\n[JsonSerializable(typeof(PaidV1))] public sealed partial class ShopJson : JsonSerializerContext;").Ids().ShouldNotContain("TMSG006");
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
