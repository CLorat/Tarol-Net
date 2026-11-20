namespace Tarol.Search.Tests;

public sealed class SnippetTests
{
    private const string O = SearchSnippets.Open;
    private const string C = SearchSnippets.Close;

    [Fact]
    public void Markers_become_offsets_and_markup_stays_text()
    {
        var (text, highlights) = SearchSnippets.Parse($"<script>x</script> {O}lake{C} and {O}glacier{C}");

        text.ShouldBe("<script>x</script> lake and glacier");
        highlights.ShouldBe([new SearchHighlight(19, 4), new SearchHighlight(28, 7)]);
    }

    [Fact]
    public void Offsets_are_utf16_code_units()
    {
        var (text, highlights) = SearchSnippets.Parse($"🌊 {O}Gletscher{C}");

        text!.Substring(highlights[0].Start, highlights[0].Length).ShouldBe("Gletscher");
        highlights[0].Start.ShouldBe(3);                                       // the emoji is two code units, as in JavaScript
    }

    [Theory]
    [InlineData("a" + C + "b", "ab", 0)]                                      // a close without an open
    [InlineData("a" + O + "b", "ab", 0)]                                      // an open without a close
    [InlineData("a" + O + C + "b", "ab", 0)]                                  // an empty range
    [InlineData(O + "a" + C + O + "b" + C, "ab", 1)]                          // adjacent ranges merge
    [InlineData(O + "a" + O + "b" + C, "ab", 1)]                              // a second open restarts
    public void Unbalanced_markers_are_dropped(string marked, string text, int count)
    {
        var (parsed, highlights) = SearchSnippets.Parse(marked);

        parsed.ShouldBe(text);
        highlights.Count.ShouldBe(count);
    }

    [Fact]
    public void No_snippet_has_no_highlights()
    {
        SearchSnippets.Parse(null).ShouldBe((null, []));
        var hit = SearchSnippets.Hit("t", Guid.Empty, Guid.Empty, "title", 1, null);
        (hit.Snippet, hit.Highlights.Count).ShouldBe((null, 0));
    }
}
