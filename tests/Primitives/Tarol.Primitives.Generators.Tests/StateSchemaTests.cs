using System.Text;
using System.Text.RegularExpressions;

namespace Tarol.Primitives.Generators.Tests;

/// <summary>
/// The schema hash: stable across compilations, file order and declaration order; changed by every change of the stored
/// JSON shape (names, member types, nesting, nested records, enum members); not changed by what is not stored.
/// </summary>
public partial class StateSchemaTests
{
    private const string Shop = """
        using System.Collections.Generic;
        using Tarol.Options;
        using Tarol.Primitives;
        using Tarol.StronglyTypedId;

        namespace Shop;

        public enum Status { Open, Closed }
        public sealed record Address(string Street, string City);

        public partial class Order : AggregateRoot<Order>
        {
            public EntitySet<Line> Lines { get; } = [];
            public EntityRef<Shipping> Shipping { get; } = new();
            public string Name { get; private set; } = "";
            public Status Status { get; private set; }
            public Option<Address> Address { get; private set; }
            private readonly List<string> _tags = [];
            public sealed record Placed(string Name) : Event;
            private void On(Placed e) => Name = e.Name;
        }

        public partial class Line : Entity<Order, Line>
        {
            public int Quantity { get; private set; }
            public Id<Shipping> Via { get; private set; }
            public sealed record Added(int Quantity) : Created;
            private void On(Added e) => Quantity = e.Quantity;
        }

        public partial class Shipping : Entity<Order, Shipping>
        {
            public sealed record Assigned : Created;
        }
        """;

    [Fact]
    public void The_Schema_Is_The_Fnv1a_Hash_Of_The_Shape()
    {
        const string shape = "tarol-state/1:{id:id,version:long,schema:int,address:global::Shop.Address{City:string,Street:string}?,name:string,"
                             + "status:global::Shop.Status{Open,Closed},tags:global::System.Collections.Generic.List<string>,"
                             + "lines:{id:id,quantity:int,via:id}[],shipping:{id:id}?}";
        Schema(Shop).ShouldBe(Fnv1a(shape));
    }

    [Fact]
    public void The_Schema_Is_Stable_Across_Compilations_And_File_Order()
    {
        var (first, second) = Split(Shop, "public partial class Line");
        Schema(Shop).ShouldBe(Schema(Shop));
        Schema(first, second).ShouldBe(Schema(Shop));
        Schema(second, first).ShouldBe(Schema(Shop));
    }

    [Theory]
    [InlineData("reordered fields", "    public string Name { get; private set; } = \"\";\n    public Status Status { get; private set; }", "    public Status Status { get; private set; }\n    public string Name { get; private set; } = \"\";")]
    [InlineData("a [Transient] field", "private readonly List<string> _tags = [];", "private readonly List<string> _tags = [];\n    [Transient] private string? _cache;")]
    [InlineData("a computed property", "private readonly List<string> _tags = [];", "private readonly List<string> _tags = [];\n    public int TagCount => _tags.Count;")]
    [InlineData("a pinned name equal to the default", "public string Name { get; private set; }", "[StateName(\"name\")] public string Name { get; private set; }")]
    [InlineData("a nullable annotation", "public string Name { get; private set; } = \"\";", "public string? Name { get; private set; } = \"\";")]
    public void Not_Stored_Changes_Keep_The_Schema(string change, string from, string to)
    {
        _ = change;
        Schema(Shop.Replace(from, to, StringComparison.Ordinal)).ShouldBe(Schema(Shop));
    }

    [Theory]
    [InlineData("a member type", "public int Quantity { get; private set; }", "public long Quantity { get; private set; }")]
    [InlineData("a renamed property", "public string Name { get; private set; }", "public string Title { get; private set; }")]
    [InlineData("a pinned name", "public string Name { get; private set; }", "[StateName(\"title\")] public string Name { get; private set; }")]
    [InlineData("a new field", "private readonly List<string> _tags = [];", "private readonly List<string> _tags = [];\n    private int _count;")]
    [InlineData("a nested record member", "public sealed record Address(string Street, string City);", "public sealed record Address(string Street, string City, string Zip);")]
    [InlineData("an enum member", "public enum Status { Open, Closed }", "public enum Status { Open, Closed, Archived }")]
    [InlineData("a renamed child collection", "public EntitySet<Line> Lines { get; } = [];", "public EntitySet<Line> Items { get; } = [];")]
    [InlineData("a set becomes a ref", "public EntityRef<Shipping> Shipping { get; } = new();", "public EntitySet<Shipping> Shipping { get; } = [];")]
    [InlineData("Option becomes plain", "public Option<Address> Address { get; private set; }", "public Address Address { get; private set; } = new(\"\", \"\");")]
    public void Shape_Changes_Change_The_Schema(string change, string from, string to)
    {
        _ = change;
        var source = Shop.Replace(from, to, StringComparison.Ordinal);
        source.ShouldNotBe(Shop);
        Schema(source).ShouldNotBe(Schema(Shop));
    }

    [Fact]
    public void Renaming_An_Entity_Class_Keeps_The_Schema()
    {
        // the class name is not in the JSON; the collection keeps its name
        var renamed = Shop.Replace("Line", "Item", StringComparison.Ordinal).Replace("EntitySet<Item> Items", "EntitySet<Item> Lines", StringComparison.Ordinal);
        Schema(renamed).ShouldBe(Schema(Shop));
    }

    [Fact]
    public void A_Replay_Only_Aggregate_Has_Schema_0()
        => Schema(Shop.Replace("public partial class Order", "[LoadByReplay] public partial class Order", StringComparison.Ordinal)).ShouldBe(0);

    private static int Schema(params string[] sources)
    {
        var generated = GeneratorTestHost.Run(GeneratorTestHost.Compile(sources)).Generated("Order.Aggregate.g.cs");
        return int.Parse(SchemaLine().Match(generated).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string, string) Split(string source, string at)
    {
        var i = source.IndexOf(at, StringComparison.Ordinal);
        var usings = source[..source.IndexOf("public enum", StringComparison.Ordinal)];
        return (source[..i], usings + source[i..]);
    }

    private static int Fnv1a(string shape)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(shape))
            hash = unchecked((hash ^ b) * 16777619u);
        var schema = (int)(hash & 0x7FFFFFFF);
        return schema == 0 ? 1 : schema;
    }

    [GeneratedRegex(@"public static int StateSchema => (\d+);")]
    private static partial Regex SchemaLine();
}
