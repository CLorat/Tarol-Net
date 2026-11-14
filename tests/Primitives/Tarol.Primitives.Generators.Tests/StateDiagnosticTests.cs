namespace Tarol.Primitives.Generators.Tests;

/// <summary>The stored-state diagnostics: an uncapturable field, a name used twice, a state attribute with no effect, a [Transient] member filled from an event.</summary>
public class StateDiagnosticTests
{
    private const string Usings = "using System; using System.Collections.Generic; using Tarol.Primitives; using Tarol.Results; using Tarol.StronglyTypedId;\n";

    private const string Shelf = """
        public partial class Shelf : AggregateRoot<Shelf>
        {
            public EntitySet<Book> Books { get; } = [];
            MEMBER
        }
        public partial class Book : Entity<Shelf, Book> { public sealed record Placed : Created; }
        """;

    // the snippets leave fields unused (CS0169, CS0067): only the Tarol diagnostics matter here
    private static string[] Ids(string source) => [.. GeneratorTestHost.Diagnose(Usings + source).Ids().Where(static id => id.StartsWith("TPRM", StringComparison.Ordinal))];

    private static string[] WithMember(string member) => Ids(Shelf.Replace("MEMBER", member, StringComparison.Ordinal));

    [Theory]
    [InlineData("private Book? _favourite;")]                                  // an entity
    [InlineData("public Book? Favourite { get; private set; }")]               // an entity, auto-property
    [InlineData("private List<Book> _stack = [];")]                            // a collection of entities
    [InlineData("private Book[] _pile = [];")]                                 // an array of entities
    [InlineData("private Dictionary<Guid, Book> _byIsbn = [];")]               // a map of entities
    [InlineData("private Shelf? _neighbour;")]                                 // a root
    [InlineData("private Func<int>? _clock;")]                                 // a delegate
    [InlineData("public event Action? Changed;")]                              // a field-like event
    [InlineData("private object? _anything;")]                                 // an untyped object
    public void TPRM024_A_Field_The_State_Cannot_Hold(string member)
        => WithMember(member).ShouldBe(["TPRM024"]);

    [Theory]
    [InlineData("[Transient] private Book? _favourite;")]
    [InlineData("private Id<Book> _favourite;")]
    [InlineData("private List<Id<Book>> _stack = [];")]
    [InlineData("private static Func<int>? Clock;")]
    [InlineData("public int Count => Books.Count;")]
    public void TPRM024_Is_Not_Reported_For_What_Is_Not_Stored_Or_Can_Be(string member)
        => WithMember(member).ShouldBeEmpty();

    [Fact]
    public void TPRM024_Is_Not_Reported_For_An_Aggregate_That_Loads_By_Replay()
        => Ids(Shelf.Replace("MEMBER", "private Func<int>? _clock;", StringComparison.Ordinal).Replace("public partial class Shelf", "[LoadByReplay] public partial class Shelf", StringComparison.Ordinal))
            .ShouldBeEmpty();

    [Fact]
    public void TPRM024_On_An_Entity()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { private Action? _onRead; public sealed record Placed : Created; }
            """).ShouldBe(["TPRM024"]);

    [Theory]
    [InlineData("private string _name = \"\"; public string Name { get; private set; } = \"\";")]  // _name and Name -> "name"
    [InlineData("[StateName(\"books\")] public int Count { get; private set; }")]                // collides with the child collection
    [InlineData("public long Version2 { get; private set; } [StateName(\"version\")] public int V { get; private set; }")] // reserved on the root
    [InlineData("[StateName(\"id\")] public int Number { get; private set; }")]                  // reserved everywhere
    public void TPRM025_A_Name_Used_Twice_Or_Reserved(string member)
        => WithMember(member).ShouldBe(["TPRM025"]);

    [Fact]
    public void TPRM025_Is_Solved_By_Pinning_A_Name()
        => WithMember("private string _name = \"\"; [StateName(\"title\")] public string Name { get; private set; } = \"\";").ShouldBeEmpty();

    [Fact]
    public void TPRM025_Version_And_Schema_Are_Free_On_An_Entity()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { public int Version { get; private set; } public int Schema { get; private set; } public sealed record Placed : Created; }
            """).ShouldBeEmpty();

    [Theory]
    [InlineData("[StateName(\"n\")] public int Count => Books.Count;")]          // no backing field
    [InlineData("[StateName(\"n\")] [Transient] private int _cache;")]           // transient
    [InlineData("[StateName(\"n\")] private static int Shared;")]               // static
    public void TPRM026_StateName_On_A_Member_That_Is_Not_Stored(string member)
        => WithMember(member).ShouldBe(["TPRM026"]);

    [Fact]
    public void TPRM026_StateName_On_An_Aggregate_That_Loads_By_Replay()
        => Ids(Shelf.Replace("MEMBER", "[StateName(\"n\")] public int Count { get; private set; }", StringComparison.Ordinal).Replace("public partial class Shelf", "[LoadByReplay] public partial class Shelf", StringComparison.Ordinal))
            .ShouldBe(["TPRM026"]);

    [Fact]
    public void TPRM026_LoadByReplay_On_An_Entity()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; }
            [LoadByReplay] public partial class Book : Entity<Shelf, Book> { public sealed record Placed : Created; }
            """).ShouldBe(["TPRM026"]);

    [Theory]
    [InlineData("_cache = e.Title;")]
    [InlineData("_count++;")]
    [InlineData("_count += 2;")]
    [InlineData("_cache ??= e.Title;")]
    public void TPRM027_A_Transient_Member_Filled_In_On(string body)
        => Ids($$"""
            public partial class Shelf : AggregateRoot<Shelf>
            {
                [Transient] private string? _cache;
                [Transient] private int _count;
                public sealed record Titled(string Title) : Event;
                private void On(Titled e) { {{body}} }
            }
            """).ShouldBe(["TPRM027"]);

    [Theory]
    [InlineData("_cache = null;")]
    [InlineData("_cache = default;")]
    [InlineData("_count = default;")]
    [InlineData("_count = 0;")]
    public void TPRM027_Resetting_A_Cache_In_On_Is_Allowed(string body)
    {
        var ids = Ids($$"""
            public partial class Shelf : AggregateRoot<Shelf>
            {
                [Transient] private string? _cache;
                [Transient] private int _count;
                public string Title { get; private set; } = "";
                public sealed record Titled(string Title) : Event;
                private void On(Titled e) { Title = e.Title; {{body}} }
            }
            """);
        // "= 0" is a constant, not a reset by default: reported, so a cache keeps its intent explicit
        if (body == "_count = 0;")
            ids.ShouldBe(["TPRM027"]);
        else
            ids.ShouldBeEmpty();
    }
}
