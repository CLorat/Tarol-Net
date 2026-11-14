namespace Tarol.Primitives.Generators.Tests;

/// <summary>One test per diagnostic (TPRM001, the generator's own failure, has no trigger by design).</summary>
public class DiagnosticTests
{
    private const string Usings = "using Tarol.Primitives; using Tarol.Results; using Tarol.StronglyTypedId;\n";

    private static string[] Ids(string source) => GeneratorTestHost.Diagnose(Usings + source).Ids();

    [Fact]
    public void TPRM002_A_Root_That_Is_Not_Partial()
        => Ids("public class Shelf : AggregateRoot<Shelf> { public Shelf() : base(Id<Shelf>.New()) { } protected override void Dispatch(in StreamEvent s) { } }")
            .ShouldContain("TPRM002");

    [Fact]
    public void TPRM002_A_Containing_Type_That_Is_Not_Partial()
        => Ids("public static class Outer { public partial class Shelf : AggregateRoot<Shelf> { } }").ShouldContain("TPRM002");

    [Fact]
    public void TPRM003_A_Generic_Entity()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { }
            public partial class Box<T> : Entity<Shelf, Box<T>> { protected Box(Id<Box<T>> id, Shelf root) : base(id, root) { } }
            """).ShouldContain("TPRM003");

    [Fact]
    public void TPRM003_An_Event_That_Derives_From_DomainEvent()
        => Ids("public sealed record Loose : DomainEvent;").ShouldBe(["TPRM003"]);

    [Fact]
    public void TPRM004_An_Event_Without_A_Handler()
        => Ids("public partial class Shelf : AggregateRoot<Shelf> { public sealed record Renamed(string Name) : Event; }").ShouldBe(["TPRM004"]);

    [Fact]
    public void TPRM004_Is_Satisfied_By_A_Handler_For_A_Base_Event()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public abstract record Changed : Event; public sealed record Renamed : Changed; private void On(Changed e) { } }
            """).ShouldBeEmpty();

    [Fact]
    public void F22_Handlers_For_A_Base_And_A_Derived_Event_Are_Ordered_Most_Derived_First()
    {
        var source = Usings + """
            public partial class Shelf : AggregateRoot<Shelf>
            {
                public abstract record Changed : Event; public record Renamed : Changed; public sealed record Retitled : Renamed;
                private void On(Changed e) { } private void On(Renamed e) { } private void On(Retitled e) { }
            }
            """;
        GeneratorTestHost.Diagnose(source).Ids().ShouldBeEmpty();   // no CS8120 (a subsumed case)
        var generated = GeneratorTestHost.Run(GeneratorTestHost.Compile(source)).Generated("Shelf.Aggregate.g.cs");
        generated.IndexOf("Shelf.Retitled e", StringComparison.Ordinal).ShouldBeLessThan(generated.IndexOf("Shelf.Renamed e", StringComparison.Ordinal));
        generated.IndexOf("Shelf.Renamed e", StringComparison.Ordinal).ShouldBeLessThan(generated.IndexOf("Shelf.Changed e", StringComparison.Ordinal));
    }

    [Fact]
    public void TPRM005_A_Handler_On_A_Type_That_Never_Receives_The_Event()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; public EntitySet<Lamp> Lamps { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { public sealed record Read : Event; private void On(Read e) { } }
            public partial class Lamp : Entity<Shelf, Lamp> { private void On(Book.Read e) { } }
            """).ShouldBe(["TPRM005"]);

    [Fact]
    public void TPRM005_A_Handler_For_Another_Aggregates_Event()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { private void On(Desk.Moved e) { } }
            public partial class Desk : AggregateRoot<Desk> { public sealed record Moved : Event; private void On(Moved e) { } }
            """).ShouldBe(["TPRM005"]);

    [Fact]
    public void TPRM006_An_Entity_Without_An_Owner()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { }
            public partial class Book : Entity<Shelf, Book> { }
            """).ShouldBe(["TPRM006"]);

    [Fact]
    public void TPRM007_A_Plain_Created_For_A_Child_With_Two_Owner_Types()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; public EntitySet<Note> Notes { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { public EntitySet<Note> Notes { get; } = []; }
            public partial class Note : Entity<Shelf, Note> { public sealed record Added : Created; }
            """).ShouldBe(["TPRM007"]);

    [Fact]
    public void TPRM007_Two_Containers_Of_One_Child_Type()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; public EntitySet<Book> More { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { }
            """).ShouldContain("TPRM007");

    [Fact]
    public void TPRM008_An_Assignable_Container()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; set; } = []; }
            public partial class Book : Entity<Shelf, Book> { }
            """).ShouldContain("TPRM008");

    [Fact]
    public void TPRM008_A_Non_Readonly_Field_Container()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { private EntitySet<Book> _books = []; }
            public partial class Book : Entity<Shelf, Book> { }
            """).ShouldContain("TPRM008");

    [Fact]
    public void TPRM009_An_Invariant_With_The_Wrong_Shape()
        => Ids("public partial class Shelf : AggregateRoot<Shelf> { [Invariant] private bool Full() => false; }").ShouldBe(["TPRM009"]);

    [Fact]
    public void TPRM010_Two_Events_With_One_Alias()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf>
            {
                [EventType("shelf.moved")] public sealed record Moved : Event;
                [EventType("shelf.moved")] public sealed record Shifted : Event;
                private void On(Moved e) { } private void On(Shifted e) { }
            }
            """).ShouldBe(["TPRM010"]);

    [Fact]
    public void TPRM011_CreatedIn_A_Type_That_Does_Not_Own_The_Child()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { }
            public partial class Lamp : Entity<Shelf, Lamp> { }
            public sealed record BookInLamp : Book.CreatedIn<Lamp>;
            """).ShouldContain("TPRM011");

    [Fact]
    public void TPRM012_A_Container_Of_Another_Aggregates_Entity()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Drawer> Drawers { get; } = []; }
            public partial class Desk : AggregateRoot<Desk> { public EntitySet<Drawer> Drawers { get; } = []; }
            public partial class Drawer : Entity<Desk, Drawer> { }
            """).ShouldBe(["TPRM012"]);

    [Fact]
    public void A_Create_Raised_By_A_Non_Owner_Does_Not_Compile()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; public EntitySet<Lamp> Lamps { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { public sealed record Added : Created; }
            public partial class Lamp : Entity<Shelf, Lamp> { public Book Wrong() => Raise(new Book.Added()); }
            """).ShouldBe(["CS0311"]);

    // ───── analyzer ─────

    private const string Counter = """
        public partial class Counter : AggregateRoot<Counter>
        {
            public int Count { get; private set; }
            [Transient] private string? _cache;
            private int _shadow;
            public sealed record Added(int By) : Event;
            public Success Add(int by) => Raise(new Added(by));
            private void On(Added e) { Count += e.By; Recalculate(); }
            private void Recalculate() => _shadow = Count * 2;
            public string Text => _cache ??= Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            public int Shadow => _shadow;
            private Counter(Id<Counter> id) : base(id) { Count = 0; }
        }
        """;

    [Fact]
    public void The_Analyzer_Allows_On_Handlers_Helpers_Constructors_And_Transient_Members()
        => Ids(Counter).ShouldBeEmpty();

    [Fact]
    public void TPRM013_State_Assigned_In_A_Behaviour()
        => Ids(Counter.Replace("public Success Add(int by)", "public void Reset() => Count = 0;\n    public void Bump() => Count++;\n    public Success Add(int by)", StringComparison.Ordinal))
            .ShouldBe(["TPRM013", "TPRM013"]);

    [Fact]
    public void TPRM013_A_Helper_Also_Called_From_A_Behaviour()
        => Ids(Counter.Replace("public Success Add(int by)", "public void Again() => Recalculate();\n    public Success Add(int by)", StringComparison.Ordinal))
            .ShouldBe(["TPRM013"]);

    [Fact]
    public void TPRM014_A_Rule_A_Throw_A_Clock_Or_A_Raise_In_An_On_Handler()
        => Ids("""
            public partial class Clock : AggregateRoot<Clock>
            {
                public System.DateTimeOffset At { get; private set; }
                public sealed record Ticked(int N) : Event;
                private void On(Ticked e)
                {
                    if (Check(e.N).IsFailure) throw new System.InvalidOperationException();
                    At = System.TimeProvider.System.GetUtcNow();
                    Raise(new Ticked(1));
                }
                private static Result Check(int n) => n > 0 ? Result.Success() : Error.Conflict("x");
            }
            """).ShouldBe(["TPRM014", "TPRM014", "TPRM014", "TPRM014"]);

    [Fact]
    public void TPRM015_Generated_Plumbing_Called_From_User_Code()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf>
            {
                public EntitySet<Book> Books { get; } = [];
                public void Sneak(StreamEvent e) { Dispatch(in e); __map0.Clear(); _ = CheckInvariants(); }
            }
            public partial class Book : Entity<Shelf, Book> { }
            """).ShouldBe(["TPRM015", "TPRM015", "TPRM015"]);
}
