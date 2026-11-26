namespace Tarol.Queries.Generators.Tests;

public sealed class DiagnosticTests
{
    private static string[] Diagnose(string body) => GeneratorTestHost.Diagnose(GeneratorTestHost.Usings + "namespace Shop;\n" + body).Ids();

    private static string Message(string body, string id)
        => GeneratorTestHost.Diagnose(GeneratorTestHost.Usings + "namespace Shop;\n" + body).Single(d => d.Id == id)
            .GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    private const string Valid = """
        { public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId); }
        """;

    [Fact]
    public void An_sql_projections_row_has_columns_and_no_When()
        => Diagnose("[Projection(\"rows\", Sql = true)] public sealed partial record Row(Guid Id, [Sort] int Count);").ShouldBeEmpty();

    [Fact]
    public void TQRY003_an_sql_projections_row_with_a_When()
        => Message("[Projection(\"rows\", Sql = true)] public sealed partial record Row(Guid Id)\n" + Valid, "TQRY003")
            .ShouldContain("an SQL projection (Sql = true) has no When methods: its ISqlProjection<Row> writes the rows");

    [Fact]
    public void TQRY001_Aggregate_on_a_row_projection()
        => Message("[Projection(\"rows\", Aggregate = typeof(Order))] public sealed partial record Row(Guid Id)\n" + Valid, "TQRY001")
            .ShouldContain("Aggregate names the read grant of an SQL projection's rows");

    [Fact]
    public void TQRY001_an_sql_projection_per_entity()
        => Message("[Projection(\"rows\", Sql = true, PerEntity = true)] public sealed partial record Row(Guid Id);", "TQRY001")
            .ShouldContain("cannot be PerEntity");

    [Fact]
    public void A_valid_projection_has_no_diagnostics()
        => Diagnose("[Projection(\"rows\")] public sealed partial record Row(Guid Id)\n" + Valid).ShouldBeEmpty();

    [Fact]
    public void TQRY001_not_partial()
        => Message("[Projection(\"rows\")] public sealed record Row(Guid Id)\n" + Valid, "TQRY001").ShouldContain("must be declared partial");

    [Fact]
    public void TQRY001_not_a_record()
        => Message("""
            [Projection("rows")] public sealed partial class Row
            { public Guid Id { get; init; } public static Row When(Order.Opened e) => new(); }
            """, "TQRY001").ShouldContain("partial record class");

    [Fact]
    public void TQRY001_a_containing_type_not_partial()
        => Message("public static class Outer { [Projection(\"rows\")] public sealed partial record Row(Guid Id)\n" + Valid + " }", "TQRY001")
            .ShouldContain("containing type 'Outer'");

    [Fact]
    public void TQRY001_generic()
        => Message("""
            [Projection("rows")] public sealed partial record Row<T>(Guid Id)
            { public static Row<T> When(Order.Opened e, RecordedEvent r) => new(r.StreamId); }
            """, "TQRY001").ShouldContain("generic");

    [Theory]
    [InlineData("Rows")]
    [InlineData("1rows")]
    [InlineData("rows_list")]
    [InlineData("")]
    [InlineData("a-very-long-projection-name-that-does-not-fit")]
    public void TQRY001_bad_name(string name)
        => Message($"[Projection(\"{name}\")] public sealed partial record Row(Guid Id)\n" + Valid, "TQRY001").ShouldContain("lowercase letters");

    [Fact]
    public void TQRY001_bad_version()
        => Message("[Projection(\"rows\", Version = 0)] public sealed partial record Row(Guid Id)\n" + Valid, "TQRY001").ShouldContain("Version 0");

    [Fact]
    public void TQRY002_no_creating_When()
        => Message("""
            [Projection("rows")] public sealed partial record Row(Guid Id)
            { public Row When(Order.WasClosed e) => this with { }; }
            """, "TQRY002").ShouldContain("no static When creates the row");

    [Fact]
    public void An_Empty_start_row_replaces_the_creating_When_and_a_wrong_one_is_reported()
    {
        // TN-54 (Tatalu FB-F01.2-2)
        Diagnose("""
            [Projection("rows")] public sealed partial record Row(Guid Id)
            {
                public static Row Empty(RecordedEvent r) => new(r.StreamId);
                public Row When(Order.WasClosed e) => this with { };
            }
            """).ShouldBeEmpty();
        Message("""
            [Projection("rows")] public sealed partial record Row(Guid Id)
            {
                public static Row Empty(Guid id) => new(id);
                public Row When(Order.WasClosed e) => this with { };
            }
            """, "TQRY003").ShouldContain("Empty(RecordedEvent r)");
    }

    [Fact]
    public void TQRY002_two_When_for_one_event()
        => Message("""
            [Projection("rows")] public sealed partial record Row(Guid Id)
            {
                public static Row When(Order.Opened e) => new(Guid.Empty);
                public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId);
            }
            """, "TQRY002").ShouldContain("two static When methods for Opened");

    [Fact]
    public void A_static_and_an_instance_When_for_one_event_are_fine()
        => Diagnose("""
            [Projection("rows")] public sealed partial record Row(Guid Id, int Opens)
            {
                public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId, 1);
                public Row When(Order.Opened e) => this with { Opens = Opens + 1 };
            }
            """).ShouldBeEmpty();

    [Fact]
    public void TQRY003_an_event_of_another_aggregate()
        => Message("""
            [Projection("rows")] public sealed partial record Row(Guid Id)
            {
                public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId);
                public Row When(Customer.Registered e) => this with { };
            }
            """, "TQRY003").ShouldContain("one aggregate; this one is of Customer, the first of Order");

    [Fact]
    public void An_entity_event_belongs_to_its_root()
        => Diagnose("""
            [Projection("rows")] public sealed partial record Row(Guid Id, int Lines)
            {
                public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId, 0);
                public Row When(Line.Added e) => this with { Lines = Lines + 1 };
            }
            """).ShouldBeEmpty();

    [Theory]
    [InlineData("public static Row When(string e) => new(Guid.Empty);", "not a concrete event")]
    [InlineData("public static Row When(Order.Opened e, int r) => new(Guid.Empty);", "second parameter must be RecordedEvent")]
    [InlineData("public static Row When(Order.Opened e, RecordedEvent r, int x) => new(Guid.Empty);", "(TEvent) or (TEvent, RecordedEvent)")]
    [InlineData("public static string When(Order.Opened e) => \"\";", "returns the row")]
    [InlineData("public static Row When<T>(Order.Opened e) => new(Guid.Empty);", "cannot be generic")]
    [InlineData("public static Row When(ref Order.Opened e) => new(Guid.Empty);", "(TEvent) or (TEvent, RecordedEvent)")]
    [InlineData("public static Row When(Tarol.Primitives.DomainEvent e) => new(Guid.Empty);", "not a concrete event")]
    public void TQRY003_a_When_of_the_wrong_shape(string when, string reason)
        => Message("[Projection(\"rows\")] public sealed partial record Row(Guid Id)\n{ public static Row When(Order.WasClosed e, RecordedEvent r) => new(r.StreamId); "
                   + when + " }", "TQRY003").ShouldContain(reason);

    [Theory]
    [InlineData("[Sort] int[] Tags", "takes set filters only")]
    [InlineData("[Sort] System.Collections.Generic.List<int> Tags", "has no column type")]
    [InlineData("[Sort] string? Note", "cannot be nullable")]
    [InlineData("[Sort] int? Count", "cannot be nullable")]
    [InlineData("[Sort] long Version", "reserved for the key")]
    public void TQRY004_a_sort_without_a_sortable_column(string member, string reason)
        => Message($"[Projection(\"rows\")] public sealed partial record Row(Guid Id, {member})\n" + """
            { public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId, default!); }
            """, "TQRY004").ShouldContain(reason);

    [Fact]
    public void Members_without_a_column_stay_in_the_jsonb_without_a_diagnostic()
        => Diagnose("""
            [Projection("rows")] public sealed partial record Row(Guid Id, int[] Tags, long Version, System.Collections.Generic.List<string> Names)
            { public static Row When(Order.Opened e, RecordedEvent r) => new(r.StreamId, [], 0, []); }
            """).ShouldBeEmpty();
}
