namespace Tarol.Changes.Generators.Tests;

public sealed class DiagnosticTests
{
    private static string[] Diagnose(string body) => GeneratorTestHost.Diagnose(GeneratorTestHost.Usings + "namespace Shop;\n" + body).Ids();

    private static string Message(string body, string id)
        => GeneratorTestHost.Diagnose(GeneratorTestHost.Usings + "namespace Shop;\n" + body).Single(d => d.Id == id).GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TCHG001_no_id_member()
        => Diagnose("""
            public sealed partial record Close(string Number) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """).ShouldContain("TCHG001");

    [Fact]
    public void TCHG001_two_id_members_names_the_ways_out()
        => Message("""
            public sealed partial record Close(Id<Order> A, Id<Order> B) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """, "TCHG001").ShouldContain("found 2; implement 'Id<Order> IChange<Order>.Target' yourself, or make it an ILoads<Order>");

    [Fact]
    public void An_explicit_target_or_ILoads_needs_no_id_member()
        => Diagnose("""
            public sealed partial record CloseExplicit(Id<Order> A, Id<Order> B) : IChange<Order>
            { Id<Order> IChange<Order>.Target => B; public Result Execute(Order o, ChangeContext c) => o.Close(); }
            public sealed record CloseLoaded(string Number) : ILoads<Order>
            {
                public ValueTask<Result<Order>> Load(IAggregateReader r, ChangeContext c, CancellationToken ct) => ValueTask.FromResult<Result<Order>>(Error.NotFound("x"));
                public Result Execute(Order o, ChangeContext c) => o.Close();
            }
            """).ShouldBeEmpty();

    [Fact]
    public void TCHG002_not_partial()
        => Message("""
            public sealed record Close(Id<Order> Order) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """, "TCHG002").ShouldBe("'Close' must be declared partial so that IChange<Order>.Target can be generated");

    [Fact]
    public void TCHG002_containing_type_not_partial()
        => Message("""
            public static class Admin
            {
                public sealed partial record Close(Id<Order> Order) : IChange<Order>
                { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            }
            """, "TCHG002").ShouldStartWith("'Admin' must be declared partial");

    [Fact]
    public void A_member_named_Target_implements_the_interface_itself()
        => Diagnose("""
            public sealed record Close(Id<Order> Target) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """).ShouldBeEmpty();

    [Fact]
    public void A_creation_without_reads_needs_no_partial()
        => Diagnose("""
            public sealed record Open : ICreation<Order> { public Result<Order> Execute(ChangeContext c) => Order.Open(); }
            """).ShouldBeEmpty();

    [Fact]
    public void TCHG003_generic_change()
        => Diagnose("""
            public sealed partial record Close<T>(Id<Order> Order) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """).ShouldContain("TCHG003");

    [Fact]
    public void TCHG003_change_and_creation_at_once()
        => Message("""
            public sealed partial record Both(Id<Order> Order) : IChange<Order>, ICreation<Order>
            {
                public Result Execute(Order o, ChangeContext c) => o.Close();
                public Result<Order> Execute(ChangeContext c) => Order.Open();
            }
            """, "TCHG003").ShouldContain("more than one of IChange<T> and ICreation<T>");

    [Fact]
    public void TCHG004_read_on_something_that_is_not_an_aggregate_id()
        => Message("""
            public sealed partial record Close(Id<Order> Order, [Read] string Customer) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """, "TCHG004").ShouldBe("[Read] on 'Customer' of 'Close': it is not an Id<X> of an aggregate");

    [Fact]
    public void TCHG004_read_of_the_own_target()
        => Message("""
            public sealed partial record Close(Id<Order> Order, [Read] Id<Order> Other) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """, "TCHG004").ShouldContain("is the change's own target");

    [Fact]
    public void TCHG004_read_of_a_type_that_is_no_aggregate()
        => Message("""
            public sealed class Note;
            public sealed partial record Close(Id<Order> Order, [Read] Id<Note> Note) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """, "TCHG004").ShouldContain("Note is not an aggregate root");

    [Fact]
    public void TCHG005_rules_that_would_not_run()
        => Message("""
            public readonly partial record struct Quantity([Range(1, 10)] int Value);
            public sealed partial record Close(Id<Order> Order, Quantity Quantity) : IChange<Order>
            { public Result Execute(Order o, ChangeContext c) => o.Close(); }
            """, "TCHG005").ShouldBe("'Close' has members with validation rules (Quantity) but does not implement IValidatable, so the pipeline cannot check them before the load");

    [Fact]
    public void An_IValidatable_change_with_rules_is_fine()
        => Diagnose("""
            public readonly partial record struct Quantity([Range(1, 10)] int Value);
            public sealed partial record Close(Id<Order> Order, Quantity Quantity) : IChange<Order>, IValidatable
            {
                public void Validate(ref ValidationErrors errors) { }
                public Result Execute(Order o, ChangeContext c) => o.Close();
            }
            """).ShouldBeEmpty();

    [Fact]
    public void A_type_named_like_the_interface_is_ignored()
        => Diagnose("""
            public interface IChange<T>;
            public sealed record NotAChange : IChange<int>;
            """).ShouldBeEmpty();
}
