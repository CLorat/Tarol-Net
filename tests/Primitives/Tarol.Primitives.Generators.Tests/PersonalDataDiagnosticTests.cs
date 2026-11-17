namespace Tarol.Primitives.Generators.Tests;

/// <summary>Step 16A: the data-protection marks, TPRM030–TPRM033 (data-protection.md §6.1).</summary>
public class PersonalDataDiagnosticTests
{
    private const string Usings = "using System; using System.Collections.Generic; using Tarol.Primitives; using Tarol.StronglyTypedId;\n";

    private const string Customer = """
        public partial class Customer : AggregateRoot<Customer>
        {
            MEMBER
            public sealed record Noted(EVENT) : Event;
            private void On(Noted e) { }
        }
        """;

    private static string[] Ids(string source)
        => [.. GeneratorTestHost.Diagnose(Usings + source).Ids().Where(static id => id.StartsWith("TPRM03", StringComparison.Ordinal))];

    private static string[] With(string member = "", string @event = "string Text")
        => Ids(Customer.Replace("MEMBER", member, StringComparison.Ordinal).Replace("EVENT", @event, StringComparison.Ordinal));

    [Theory]
    [InlineData("[PersonalData] public string? Name { get; private set; }", "string Text")]
    [InlineData("[PersonalData] public int? Age { get; private set; }", "string Text")]
    [InlineData("[PersonalData] private List<string>? _tags;", "string Text")]
    [InlineData("", "[property: PersonalData] string? Email")]
    [InlineData("", "[PersonalData] string? Email")]                                        // the parameter reads as the property
    [InlineData("", "Guid Customer, [property: PersonalData(Subject = nameof(Customer))] string? ShipTo")]
    [InlineData("", "Id<Customer> Customer, [property: PersonalData(Subject = nameof(Customer))] string? ShipTo")]
    [InlineData("public Guid Partner { get; private set; } [PersonalData(Subject = nameof(Partner))] public string? Note { get; private set; }", "string Text")]
    [InlineData("[PersonalData(Subject = nameof(Id))] public string? Note { get; private set; }", "string Text")]
    public void A_Valid_Mark_Reports_Nothing(string member, string @event)
        => With(member, @event).ShouldBeEmpty();

    [Theory]
    [InlineData("[PersonalData] public string Name { get; private set; } = \"\";", "string Text")]
    [InlineData("[PersonalData] public int Age { get; private set; }", "string Text")]
    [InlineData("", "[property: PersonalData] string Email")]
    [InlineData("", "[PersonalData] decimal Income")]
    public void TPRM030_A_Personal_Member_Must_Be_Nullable(string member, string @event)
        => With(member, @event).ShouldBe(["TPRM030"]);

    [Theory]
    [InlineData("", "string Customer, [property: PersonalData(Subject = nameof(Customer))] string? ShipTo")]   // not a Guid
    [InlineData("", "Guid? Customer, [property: PersonalData(Subject = nameof(Customer))] string? ShipTo")]    // nullable
    [InlineData("", "[property: PersonalData(Subject = \"Nobody\")] string? ShipTo")]                          // no such member
    [InlineData("", "[property: PersonalData(Subject = nameof(ShipTo))] string? ShipTo")]                      // itself
    public void TPRM031_The_Subject_Names_A_Guid_Or_Id_Member(string member, string @event)
        => With(member, @event).ShouldBe(["TPRM031"]);

    [Theory]
    [InlineData("public sealed record Address([property: PersonalData] string? Street);")]                 // a value object
    [InlineData("public class Settings { [PersonalData] public string? Owner { get; set; } }")]              // any other type
    public void TPRM032_A_Mark_Outside_Events_State_And_Rows(string type)
        => Ids(Customer.Replace("MEMBER", "", StringComparison.Ordinal).Replace("EVENT", "string Text", StringComparison.Ordinal) + type).ShouldBe(["TPRM032"]);

    [Theory]
    [InlineData("[Transient, PersonalData] private string? _cache;")]
    [InlineData("[PersonalData] public static string? Shared { get; set; }")]
    [InlineData("[PersonalData] public string? Computed => null;")]
    public void TPRM032_A_Member_That_Is_Not_Stored(string member)
        => With(member).ShouldBe(["TPRM032"]);

    [Fact]
    public void TPRM032_The_State_Of_An_Aggregate_That_Loads_By_Replay()
        => Ids(Customer.Replace("MEMBER", "[PersonalData] public string? Name { get; private set; }", StringComparison.Ordinal)
                .Replace("EVENT", "string Text", StringComparison.Ordinal)
                .Replace("public partial class Customer", "[LoadByReplay] public partial class Customer", StringComparison.Ordinal))
            .ShouldBe(["TPRM032"]);

    [Fact]
    public void A_Mark_On_An_Entity_Is_Valid()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; }
            public partial class Book : Entity<Shelf, Book> { [PersonalData] public string? Reader { get; private set; } public sealed record Placed : Created; }
            """).ShouldBeEmpty();

    [Theory]
    [InlineData("[Retention(Days = 0)] public partial class Customer")]
    [InlineData("[Retention] public partial class Customer")]
    public void TPRM033_Retention_Needs_At_Least_One_Day(string declaration)
        => Ids(Customer.Replace("public partial class Customer", declaration, StringComparison.Ordinal).Replace("MEMBER", "", StringComparison.Ordinal).Replace("EVENT", "string Text", StringComparison.Ordinal))
            .ShouldBe(["TPRM033"]);

    [Fact]
    public void TPRM033_Retention_Belongs_On_A_Root()
        => Ids("""
            public partial class Shelf : AggregateRoot<Shelf> { public EntitySet<Book> Books { get; } = []; }
            [Retention(Days = 30)] public partial class Book : Entity<Shelf, Book> { public sealed record Placed : Created; }
            """).ShouldBe(["TPRM033"]);

    [Fact]
    public void A_Valid_Retention_Reports_Nothing()
        => Ids(Customer.Replace("public partial class Customer", "[Retention(Days = 3650)] public partial class Customer", StringComparison.Ordinal).Replace("MEMBER", "", StringComparison.Ordinal).Replace("EVENT", "string Text", StringComparison.Ordinal))
            .ShouldBeEmpty();
}
