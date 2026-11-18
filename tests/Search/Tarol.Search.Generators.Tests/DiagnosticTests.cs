namespace Tarol.Search.Generators.Tests;

public sealed class DiagnosticTests
{
    private static string Aggregate(string members, string attribute = "[Searchable]", string modifiers = "public partial class") => GeneratorTestHost.Usings + $$"""
        namespace Gallery;
        {{attribute}}
        {{modifiers}} Artwork : AggregateRoot<Artwork>
        {
            {{members}}
            public sealed record Created(string Title) : Event;
            private void On(Created e) { }
        }
        """;

    private static string[] Ids(string source) => GeneratorTestHost.Diagnose(source).Ids();

    private static string Message(string source, string id)
        => GeneratorTestHost.Diagnose(source).Single(d => d.Id == id).GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_valid_aggregate_has_no_diagnostics()
        => Ids(Aggregate("[SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = \"\";")).ShouldBeEmpty();

    [Fact]
    public void TSRCH001_not_partial()
        => Message(Aggregate("[Searchable] public string Title { get; private set; } = \"\";", modifiers: "public class"), "TSRCH001")
            .ShouldContain("must be declared partial");

    [Fact]
    public void TSRCH001_not_an_aggregate()
        => Message(GeneratorTestHost.Usings + "namespace Gallery; [Searchable] public partial class Note { [Searchable] public string Text { get; set; } = \"\"; }", "TSRCH001")
            .ShouldContain("only an aggregate");

    [Fact]
    public void TSRCH002_a_member_without_text()
        => Message(Aggregate("[Searchable] public Dictionary<string, int> Counts { get; } = [];"), "TSRCH002").ShouldContain("has no text");

    [Fact]
    public void TSRCH002_an_audience_that_is_no_string_or_guid()
        => Message(Aggregate("[Searchable] public string Title { get; private set; } = \"\"; [SearchAudience] public int Level { get; private set; }"), "TSRCH002")
            .ShouldContain("[SearchAudience] needs");

    [Fact]
    public void TSRCH003_personal_data_as_text()
        => Message(Aggregate("[PersonalData, Searchable] public string? Owner { get; private set; }"), "TSRCH003").ShouldContain("Exact = true");

    [Fact]
    public void TSRCH003_personal_data_as_a_title()
        => Ids(Aggregate("[PersonalData, SearchTitle] public string? Owner { get; private set; } [Searchable] public string Title { get; private set; } = \"\";"))
            .ShouldContain("TSRCH003");

    [Fact]
    public void Personal_data_as_an_exact_token_is_fine()
        => Ids(Aggregate("[PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; }")).ShouldBeEmpty();

    [Fact]
    public void TSRCH004_two_titles()
        => Message(Aggregate("[SearchTitle] public string Title { get; private set; } = \"\"; [SearchTitle] public string Name { get; private set; } = \"\";"), "TSRCH004")
            .ShouldContain("second [SearchTitle]");

    [Fact]
    public void TSRCH004_an_audience_on_an_entity()
        => Message(Aggregate("public EntitySet<Edition> Editions { get; } = []; [Searchable] public string Title { get; private set; } = \"\";")
            + "\npublic partial class Edition : Entity<Artwork, Edition> { [Searchable] public string Label { get; private set; } = \"\"; [SearchAudience] public string Who { get; private set; } = \"\"; public sealed record Added : Created; private void On(Added e) { } }",
            "TSRCH004").ShouldContain("belongs on the aggregate");

    [Fact]
    public void TSRCH005_nothing_searchable()
        => Ids(Aggregate("public string Title { get; private set; } = \"\";")).ShouldBe(["TSRCH005"]);
}
