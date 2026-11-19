namespace Tarol.Search.Tests;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("  Café   Müller ", "cafe muller")]
    [InlineData("Straße", "strasse")]
    [InlineData("ADA\tLovelace", "ada lovelace")]
    [InlineData("", "")]
    public void Normalize_lowercases_folds_accents_and_spaces(string text, string expected) => SearchText.Normalize(text).ShouldBe(expected);

    [Fact]
    public void Words_split_at_everything_that_is_not_a_letter_or_digit()
        => SearchText.Words("Sunset-over the harbour, 1897!").ShouldBe(["sunset", "over", "the", "harbour", "1897"]);

    [Fact]
    public void Parse_reads_words_phrases_exclusions_and_or()
    {
        var clauses = SearchText.Parse("sunset \"harbour light\" -night dawn OR dusk");

        clauses.Count.ShouldBe(4);
        clauses[0].Alternatives.ShouldHaveSingleItem().ShouldBe(["sunset"]);
        clauses[1].Alternatives.ShouldHaveSingleItem().ShouldBe(["harbour", "light"]);
        clauses[2].Excluded.ShouldBeTrue();
        clauses[3].Alternatives.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("'); DROP TABLE x; --")]
    [InlineData("\"unclosed phrase")]
    [InlineData("OR OR -")]
    [InlineData("")]
    public void Parse_never_fails(string text) => Should.NotThrow(() => SearchText.Parse(text));

    [Fact]
    public void A_cursor_belongs_to_its_query_and_version()
    {
        var request = new SearchRequest(Guid.NewGuid(), "harbour") { Types = ["artwork"] };
        var cursor = SearchText.Cursor(request, 1, 20);

        SearchText.Offset(request with { After = cursor }, 1, out var offset).ShouldBeNull();
        offset.ShouldBe(20);
        SearchText.Offset(request with { After = cursor }, 2, out _).ShouldNotBeNull();
        SearchText.Offset(request with { Text = "boats", After = cursor }, 1, out _).ShouldNotBeNull();
        SearchText.Offset(request with { Types = ["artist"], After = cursor }, 1, out _).ShouldNotBeNull();
        SearchText.Offset(request with { Tenant = Guid.NewGuid(), After = cursor }, 1, out _).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("harbour", "harbuor", true)]    // a swap of neighbours
    [InlineData("harbour", "harbor", true)]     // a deletion
    [InlineData("harbour", "harbours", true)]   // an insertion
    [InlineData("harbour", "harboxr", true)]    // a substitution
    [InlineData("harbour", "hrabuor", false)]   // two edits
    [InlineData("mountain", "moun", false)]     // a prefix is not a typo
    [InlineData("cat", "cut", false)]           // short words are not typo-matched
    public void The_typo_rule_allows_one_edit_between_words_of_four_letters(string a, string b, bool expected)
    {
        SearchText.WithinOneEdit(a, b).ShouldBe(expected);
        SearchText.WithinOneEdit(b, a).ShouldBe(expected);
    }

    [Fact]
    public void No_cursor_at_the_depth_limit() => SearchText.Cursor(new SearchRequest(Guid.Empty, "x"), 1, SearchLimits.MaxDepth).ShouldBeNull();

    [Fact]
    public void Limit_and_text_length_are_checked()
    {
        SearchText.Validate(new SearchRequest(Guid.Empty, "x") { Limit = SearchLimits.MaxLimit + 1 }).ShouldNotBeNull();
        SearchText.Validate(new SearchRequest(Guid.Empty, new string('x', SearchLimits.MaxText + 1))).ShouldNotBeNull();
        SearchText.Validate(new SearchRequest(Guid.Empty, "x")).ShouldBeNull();
    }
}
