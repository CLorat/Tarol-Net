using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tarol.Testing.Generators;

namespace Tarol.Changes.AspNetCore.Generators.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public void Entries_get_their_delegates_binders_views_and_webhooks()
        => Snapshot.Match("endpoints", GeneratorTestHost.Run(GeneratorTestHost.Compile(false, GeneratorTestHost.Domain, Sources.Entries)));

    [Fact]
    public void Lists_get_their_delegates_and_openapi_parameters()
        => Snapshot.Match("lists", GeneratorTestHost.Run(GeneratorTestHost.Compile(true, GeneratorTestHost.Domain, Sources.Lists)));

    [Fact]
    public void The_generated_lists_compile_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(true, Sources.Lists).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void With_OpenApi_referenced_the_transformers_are_generated_too()
        => Snapshot.Match("openapi", GeneratorTestHost.Run(GeneratorTestHost.Compile(true, GeneratorTestHost.Domain, Sources.Entries)));

    [Fact]
    public void The_generated_code_compiles_without_warnings_with_and_without_OpenApi()
    {
        string.Join("\n", GeneratorTestHost.Diagnose(false, Sources.Entries).Select(d => d.ToString())).ShouldBeEmpty();
        string.Join("\n", GeneratorTestHost.Diagnose(true, Sources.Entries).Select(d => d.ToString())).ShouldBeEmpty();
    }

    [Fact]
    public void With_the_UI_push_referenced_the_topics_are_in_the_document()            // BC-25: x-tarol-topics
        => Snapshot.Match("topics", GeneratorTestHost.Run(GeneratorTestHost.Compile(true, true, GeneratorTestHost.Domain, Sources.Entries)));

    [Fact]
    public void The_topics_transformer_compiles_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(true, true, Sources.Entries).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void The_problem_codes_name_what_can_happen()                // TN-30, TN-38 (Tatalu FB-F04.1-2, FB-F17.6-1, FB-F01.1-9)
    {
        const string source = GeneratorTestHost.Usings + """
            namespace Shop;

            public partial class Customer : AggregateRoot<Customer>
            {
                public sealed record Joined : Event;
                private void On(Joined e) { }
            }

            [Endpoint("POST /orders/{order}/items")]
            public sealed partial record AddItems(Id<Order> Order, [property: Read] Id<Customer> Buyer, int Items) : IChange<Order>
            {
                public Result Execute(Order order, ChangeContext context) => Items > 9 ? Invalid.Field("items", "Order.TooMany") : order.Add(Items);
            }

            [Endpoint("PUT /orders/{order}/start")]
            public sealed partial record StartOrder(Id<Order> Order) : ILoads<Order>
            {
                public async System.Threading.Tasks.ValueTask<Result<Order>> Load(Tarol.Persistence.IAggregateStore store, ChangeContext context, System.Threading.CancellationToken ct)
                    => (await store.Load(Order, context.Operation, ct)).TryGetValue(out Order? o) ? o : Order.Open();
                public Result Execute(Order order, ChangeContext context) => order.Add(0);
            }
            """;
        var generated = string.Join("\n", GeneratorTestHost.Run(GeneratorTestHost.Compile(false, GeneratorTestHost.Domain, source)).GetRunResult()
            .GeneratedTrees.Select(t => t.GetText(TestContext.Current.CancellationToken).ToString()));

        // [Read] target and an Invalid.Field code of the change; ILoads that loads or starts: no Order.NotFound
        generated.ShouldContain("""Errors = new string[] { "Auth.Forbidden", "Customer.NotFound", "Idempotency.DuplicateKey", "Idempotency.KeyReused", "Order.NotFound", "Order.TooMany", "Stream.VersionConflict" },""");
        generated.ShouldContain("""Errors = new string[] { "Auth.Forbidden", "Idempotency.DuplicateKey", "Idempotency.KeyReused", "Stream.VersionConflict" },""");
    }

    [Fact]
    public void The_static_problem_codes_of_a_change_are_its_metadata()            // BC-3: x-tarol-errors
    {
        const string source = GeneratorTestHost.Usings + """
            namespace Shop;

            [Endpoint("POST /orders/{order}/ship")]
            public sealed record ShipOrder(Id<Order> Order, int Items) : IChange<Order>
            {
                private const string Late = "Order.Late";

                public Result Execute(Order order, ChangeContext context) => context.Now.Hour > 20 ? Error.Conflict(Late) : Check(order, Items);

                private static Result Check(Order order, int items)
                    => items > 9 ? Error.Conflict(detail: "too many", code: "Order.TooMany")
                     : items < 0 ? Error.Forbidden()
                     : items == 0 ? Error.Conflict("Order." + order.Id)
                     : order.Add(items);
            }
            """;
        var generated = string.Join("\n", GeneratorTestHost.Run(GeneratorTestHost.Compile(false, GeneratorTestHost.Domain, source)).GetRunResult()
            .GeneratedTrees.Select(t => t.GetText(TestContext.Current.CancellationToken).ToString()));

        generated.ShouldContain("""Errors = new string[] { "Auth.Forbidden", "Idempotency.DuplicateKey", "Idempotency.KeyReused", "Order.Late", "Order.NotFound", "Order.TooMany", "Stream.VersionConflict" },""");
        generated.ShouldContain("""new("order", "id", web.Names(("Order", null))[0]) { Aggregate = "Order" }""");
    }

    [Fact]
    public void An_assembly_without_entries_gets_nothing()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(true, GeneratorTestHost.Domain)).GetRunResult().GeneratedTrees.ShouldBeEmpty();

    [Fact]
    public void Without_OpenApi_no_transformers_are_generated()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(false, GeneratorTestHost.Domain, Sources.Entries)).GetRunResult().GeneratedTrees
            .ShouldNotContain(t => t.FilePath.EndsWith("GeneratedOpenApi.g.cs", StringComparison.Ordinal));

    private static (GeneratorDriver Driver, CSharpCompilation Compilation) First()
    {
        var compilation = GeneratorTestHost.Compile(false, GeneratorTestHost.Domain, Sources.Entries);
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
    public void Editing_an_Execute_body_regenerates_nothing()
    {
        var (driver, compilation) = First();
        var edited = Sources.Entries.Replace("=> order.Add(Count);", "=> order.Add(Count + 0);", StringComparison.Ordinal);
        var next = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(t => t.FilePath == "Source1.cs"),
            CSharpSyntaxTree.ParseText(edited, GeneratorTestHost.Parse, "Source1.cs", cancellationToken: TestContext.Current.CancellationToken));

        Incrementality.RegeneratedOutputs(driver.RunGenerators(next, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

public sealed class DiagnosticTests
{
    private static string[] Diagnose(string body) => GeneratorTestHost.Diagnose(false, GeneratorTestHost.Usings + "namespace Shop;\n" + body).Ids();

    private static string Message(string body, string id)
        => GeneratorTestHost.Diagnose(false, GeneratorTestHost.Usings + "namespace Shop;\n" + body).First(d => d.Id == id).GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    private const string Close = " : IChange<Order> { public Result Execute(Order o, ChangeContext c) => o.Add(1); }";

    [Theory]
    [InlineData("orders", "write the verb")]
    [InlineData("POST orders", "write the verb")]
    [InlineData("FETCH /orders/{order}", "'FETCH' is not GET")]
    public void TWEB001_the_route_is_a_verb_and_a_pattern(string route, string message)
        => Message($$"""[Endpoint("{{route}}")] public sealed partial record C(Id<Order> Order)""" + Close, "TWEB001").ShouldContain(message);

    [Fact]
    public void TWEB002_only_changes_and_views_are_entries()
    {
        Message("""[Endpoint("POST /x")] public sealed record NotAChange(int X);""", "TWEB002").ShouldContain("neither IChange<T>");
        Message("""[Endpoint("GET /orders/{order}")] public sealed partial record C(Id<Order> Order)""" + Close, "TWEB002").ShouldContain("not read with GET");
        Message("""[Endpoint("POST /orders/{order}/x", RequiresScope = true)] public sealed partial record C(Id<Order> Order)""" + Close, "TWEB002")
            .ShouldContain("RequiresScope applies to list queries");
        Message("""
            [Endpoint("POST /orders/{order}")] public sealed record V(Guid Id) : IView<Order, V> { public static V From(Order o) => new(o.Id.Value); }
            """, "TWEB002").ShouldContain("read with GET");
        Message("""
            [Endpoint("GET /items/{item}")] public partial class Item : AggregateRoot<Item> { public sealed record Made : Event; private void On(Made e) { } }
            """, "TWEB002").ShouldContain("stored state is not an API");
    }

    [Fact]
    public void A_capability_link_is_a_change_without_a_policy_with_the_tenant_and_the_token_in_its_route()
    {
        Diagnose("""[Endpoint("POST /links/{tenant}/{token}/close", Capability = true)] public sealed partial record C(Id<Order> Order)""" + Close).ShouldBeEmpty();
        Message("""[Endpoint("POST /links/{tenant}/close", Capability = true)] public sealed partial record C(Id<Order> Order)""" + Close, "TWEB003")
            .ShouldContain("names the tenant and the token in its route");
        Message("""[Endpoint("POST /links/{tenant}/{token}", Capability = true, Policy = "p")] public sealed partial record C(Id<Order> Order)""" + Close, "TWEB002")
            .ShouldContain("has no Policy");
        Message("""
            [Endpoint("GET /links/{tenant}/{token}", Capability = true)] public sealed record V(Guid Id) : IView<Order, V> { public static V From(Order o) => new(o.Id.Value); }
            """, "TWEB002").ShouldContain("a change or a creation, not a read");
    }

    [Fact]
    public void TWEB003_route_values_bind_supported_members()
    {
        Message("""[Endpoint("POST /orders/{order}/{other}")] public sealed partial record C(Id<Order> Order)""" + Close, "TWEB003").ShouldContain("'{other}' is not a member");
        Message("""[Endpoint("POST /orders/{order}/{when}")] public sealed partial record C(Id<Order> Order, DateTimeOffset When)""" + Close, "TWEB003").ShouldContain("route members are Id<T>");
        Message("""
            [Endpoint("GET /orders")] public sealed record V(Guid Id) : IView<Order, V> { public static V From(Order o) => new(o.Id.Value); }
            """, "TWEB003").ShouldContain("exactly one value");
    }

    [Fact]
    public void TWEB004_two_entries_share_a_route()
        => Diagnose("""[Endpoint("POST /orders/{order}/a")] public sealed partial record A(Id<Order> Order)""" + Close + "\n"
                    + """[Endpoint("POST /orders/{id}/a")] public sealed partial record B(Id<Order> Id)""" + Close).ShouldContain("TWEB004");

    [Fact]
    public void TWEB005_webhook_factories()
    {
        Message("""
            public sealed record P(Guid Order);
            public sealed partial record C(Id<Order> Order) : IChange<Order>
            {
                [Webhook("carrier", "done")] public C From(P p) => new((Id<Order>)p.Order);
                public Result Execute(Order o, ChangeContext c) => o.Add(1);
            }
            """, "TWEB005").ShouldContain("public static method");
        Message("""
            public sealed record P(Guid Order);
            public sealed partial record C(Id<Order> Order) : IChange<Order>
            {
                [Webhook("carrier", "done")] public static C From(P p) => new((Id<Order>)p.Order);
                [Webhook("carrier", "done")] public static C From2(P p) => new((Id<Order>)p.Order);
                public Result Execute(Order o, ChangeContext c) => o.Add(1);
            }
            """, "TWEB005").ShouldContain("same (sender, type, version none)");
        Message("""
            public sealed record P(Guid Order);
            public static class NotAChange { [Webhook("carrier", "done")] public static int From(P p) => 1; }
            """, "TWEB005").ShouldContain("returns its change");
    }

    [Fact]
    public void TWEB006_an_input_needs_a_constructor_for_its_members()
        => Diagnose("""
            [Endpoint("POST /orders/{order}/note")]
            public sealed partial record C : IChange<Order>
            {
                public Id<Order> Order { get; init; }
                public Id<Order> Target => Order;
                public Result Execute(Order o, ChangeContext c) => o.Add(1);
            }
            """).ShouldContain("TWEB006");

    [Theory]
    [InlineData("public sealed record Q(int? Nope) : IQuery<OrderRow>;", "has no typed column 'Nope'")]
    [InlineData("public sealed record Q(int Items) : IQuery<OrderRow>;", "a filter member is nullable")]
    [InlineData("public sealed record Q(long? Items) : IQuery<OrderRow>;", "holds int, not long")]
    [InlineData("public sealed record Q(S Sort = S.Customer) : IQuery<OrderRow>; public enum S { Customer }", "names no [Sort] member")]
    [InlineData("public sealed record Q([FromClaim(\"sub\")] OrderState State) : IQuery<OrderRow>;", "a string or a Guid")]
    [InlineData("public sealed record Q([AtLeast(\"Nope\")] int? From) : IQuery<OrderRow>;", "has no typed column 'Nope' for the [AtLeast] bound")]  // TN-44
    [InlineData("public sealed record Q([AtMost(nameof(OrderRow.Items))] long? Until) : IQuery<OrderRow>;", "holds int, not long")]
    [InlineData("public sealed record Q([AtMost(nameof(OrderRow.Items))] int Until) : IQuery<OrderRow>;", "a filter member is nullable")]
    public void TQRY005_query_members_name_columns(string record, string message)
        => Message("[Endpoint(\"GET /q\")] " + record, "TQRY005").ShouldContain(message);

    [Fact]
    public void TWEB007_an_option_member_with_a_default_warns()                 // CC-5
    {
        Message("""[Endpoint("POST /orders/{order}/note")] public sealed record Note(Id<Order> Order, Tarol.Options.Option<string> Text = default) : IChange<Order> { public Result Execute(Order o, ChangeContext c) => o.Add(1); }""", "TWEB007")
            .ShouldContain("'Note.Text' is an Option<T> with a default value");
        Message("""[Endpoint("GET /orders/{order}")] public sealed record OrderView(Guid Id, Tarol.Options.Option<int> Items = default) : IView<Order, OrderView> { public static OrderView From(Order o) => new(o.Id.Value); }""", "TWEB007")
            .ShouldContain("'OrderView.Items'");
        Diagnose("""[Endpoint("POST /orders/{order}/note")] public sealed record Note(Id<Order> Order, Tarol.Options.Option<string> Text) : IChange<Order> { public Result Execute(Order o, ChangeContext c) => o.Add(1); }""")
            .ShouldNotContain("TWEB007");
    }

    [Fact]
    public void TQRY006_a_claim_member_named_by_the_route_warns()
        => Message("""[Endpoint("GET /c/{customer}")] public sealed record Q([FromClaim("sub")] string Customer) : IQuery<OrderRow>;""", "TQRY006")
            .ShouldContain("the claim wins");

    [Fact]
    public void A_list_is_read_with_GET()
        => Message("""[Endpoint("POST /q")] public sealed record Q(string? Customer) : IQuery<OrderRow>;""", "TWEB002").ShouldContain("read with GET");

    [Fact]
    public void Valid_entries_report_nothing()
        => Diagnose("""[Endpoint("POST /orders/{order}/a")] public sealed partial record A(Id<Order> Order)""" + Close).ShouldBeEmpty();
}

/// <summary>The list scope example of skills/tarol-list-endpoints/SKILL.md (TN-6), read from the file: every API name compiles.</summary>
public sealed class ListScopeSkillExampleTests
{
    [Fact]
    public void The_scope_example_compiles_with_the_generators()
    {
        var skill = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../src/Changes/Tarol.Changes.AspNetCore/skills/tarol-list-endpoints/SKILL.md"));
        var start = skill.IndexOf("[Endpoint(\"GET /logs\"", StringComparison.Ordinal);
        var example = skill[start..skill.IndexOf("```", start, StringComparison.Ordinal)];
        var source = GeneratorTestHost.Usings + """
            using System.Collections.Generic;
            using System.Security.Claims;
            using System.Threading;
            using System.Threading.Tasks;
            namespace Shop;

            [Projection("log-list")]
            public sealed partial record LogRow(Guid Id, Guid Child, Guid[] Tagged)
            {
                public static LogRow When(Order.Opened e, RecordedEvent r) => new(r.StreamId, Guid.Empty, []);
            }

            """ + example;

        GeneratorTestHost.Diagnose(false, source).Select(d => d.ToString()).ShouldBeEmpty();
    }
}
