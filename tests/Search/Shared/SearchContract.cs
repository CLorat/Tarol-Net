using System.Security.Cryptography;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.StronglyTypedId;

namespace Tarol.Search.Contract;

/// <summary>
/// The contract every <see cref="ISearchIndex"/> passes (docs/migration/search.md §3, §11): tenant first, types, ranking, AND,
/// phrases, exclusions, OR, prefix, typos, counts, snippets, cursor pages, entities through their aggregate, re-index on change,
/// audience, blind tokens, erasure, retention, export, the fingerprint guard and the checkpoint guard. An implementation's test
/// class derives from it and creates a fresh, empty index per test.
/// </summary>
public abstract class SearchContract
{
    protected static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-0000000000aa");
    protected static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-0000000000bb");
    protected static Option<Id<ITenant>> A => TenantA;
    protected static Option<Id<ITenant>> B => TenantB;

    /// <summary>A fresh, empty index.</summary>
    protected abstract Task<ISearchIndex> NewIndex();

    /// <summary>Waits until the index shows what was written (an engine's refresh); in-memory and PostgreSQL: nothing.</summary>
    protected virtual Task Settle(ISearchIndex index) => Task.CompletedTask;

    private InMemoryKeyStore? _keys;

    protected async Task<SearchTest> Test(bool keys = false)
    {
        _keys = keys ? new InMemoryKeyStore(new ConfiguredKeyEncryptionKey("contract", RandomNumberGenerator.GetBytes(32)), TimeProvider.System) : null;
        return SearchTest.Create(s => s.Add<Artwork>().Add<Artist>(), await NewIndex(), _keys, keys ? ContractJson.Codec() : null);
    }

    protected async Task Index(SearchTest test)
    {
        await test.CatchUp();
        await Settle(test.Index);
    }

    protected static async Task<SearchPage> Ok(SearchTest test, SearchQuery query, Option<Id<ITenant>> tenant = default)
        => (await test.Find(query, tenant)).GetValueOrThrow();

    protected static async Task<SearchPage> Ok(SearchTest test, string text, Option<Id<ITenant>> tenant = default) => await Ok(test, new SearchQuery(text), tenant);

    [Fact]
    public async Task Tenant_first_a_tenant_never_sees_another_tenants_documents()
    {
        var test = await Test();
        var mine = Artwork.Create("Sunset over harbour", "boats at dusk");
        test.Given(mine, A).Given(Artwork.Create("Sunset over harbour", "the same words in B"), B);
        await Index(test);

        var a = await Ok(test, "harbour", A);
        a.Items.ShouldHaveSingleItem().Id.ShouldBe(mine.Id.Value);
        (await Ok(test, "harbour", B)).Items.ShouldHaveSingleItem().Id.ShouldNotBe(mine.Id.Value);
        (await Ok(test, "harbour")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_stream_type_is_not_indexed()
    {
        var test = await Test();
        test.Given(Note.Write("harbour note"), A);
        await Index(test);

        (await Ok(test, "harbour", A)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_entity_is_found_by_its_own_field_and_shown_through_its_aggregate()
    {
        var test = await Test();
        var art = Artwork.Create("Sunset over harbour", "boats");
        var edition = art.AddEdition("museum edition");
        test.Given(art, A);
        await Index(test);

        var hit = (await Ok(test, "museum", A)).Items.ShouldHaveSingleItem();
        hit.Type.ShouldBe(Artwork.StreamType + ".edition");
        hit.Id.ShouldBe(edition.Id.Value);
        hit.Aggregate.ShouldBe(art.Id.Value);
        hit.Title.ShouldBe("Sunset over harbour");
    }

    [Fact]
    public async Task The_type_filter_runs_in_the_index_and_counts_are_exact()
    {
        var test = await Test();
        var art = Artwork.Create("Harbour at noon", "light");
        art.AddEdition("harbour print");
        test.Given(art, A).Given(Artist.Create("Harbour painter", "paints the sea"), A);
        await Index(test);

        var all = await Ok(test, new SearchQuery("harbour") { Counts = true }, A);
        all.Items.Count.ShouldBe(3);
        all.Counts.ShouldNotBeNull();
        all.Counts![Artwork.StreamType].ShouldBe(1);
        all.Counts[Artwork.StreamType + ".edition"].ShouldBe(1);
        all.Counts[Artist.StreamType].ShouldBe(1);
        all.CountsCapped.ShouldBeFalse();
        var artists = await Ok(test, new SearchQuery("harbour") { Types = [Artist.StreamType] }, A);
        artists.Items.ShouldHaveSingleItem().Type.ShouldBe(Artist.StreamType);
        (await test.Find(new SearchQuery("harbour") { Types = ["nope"] }, A)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task A_title_match_ranks_above_a_body_match_and_all_words_must_match()
    {
        var test = await Test();
        var body = Artwork.Create("Lot seven", "a harbour study in light");
        var title = Artwork.Create("Harbour light", "a study");
        test.Given(body, A).Given(title, A).Given(Artwork.Create("Harbour", "night"), A);
        await Index(test);

        var page = await Ok(test, "harbour light", A);
        page.Items.Select(h => h.Id).ShouldBe([title.Id.Value, body.Id.Value]);
    }

    [Fact]
    public async Task Phrases_exclusions_and_or()
    {
        var test = await Test();
        var a = Artwork.Create("Harbour light", "evening");
        var b = Artwork.Create("Light over the harbour", "morning");
        var c = Artwork.Create("Glaciers", "ice");
        test.Given(a, A).Given(b, A).Given(c, A);
        await Index(test);

        (await Ok(test, "\"harbour light\"", A)).Items.ShouldHaveSingleItem().Id.ShouldBe(a.Id.Value);
        (await Ok(test, "harbour -morning", A)).Items.ShouldHaveSingleItem().Id.ShouldBe(a.Id.Value);
        (await Ok(test, "evening OR glaciers", A)).Items.Select(h => h.Id).Order().ShouldBe(new[] { a.Id.Value, c.Id.Value }.Order());
        (await Ok(test, "'); DROP TABLE x; --", A)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_prefix_on_the_last_word()
    {
        var test = await Test();
        var lake = Artwork.Create("Mountain lake", "glaciers");
        test.Given(lake, A);
        await Index(test);

        (await Ok(test, "moun", A)).Items.ShouldBeEmpty();
        (await Ok(test, new SearchQuery("moun") { Prefix = true }, A)).Items.ShouldHaveSingleItem().Id.ShouldBe(lake.Id.Value);
    }

    [Fact]
    public async Task A_typo_still_finds_when_nothing_else_does()
    {
        var test = await Test();
        var art = Artwork.Create("Sunset over harbour", "boats");
        art.AddEdition("museum print");                                          // shows its aggregate's title, but is not found by it
        test.Given(art, A);
        await Index(test);

        (await Ok(test, "harbuor", A)).Items.ShouldHaveSingleItem().Id.ShouldBe(art.Id.Value);
    }

    [Fact]
    public async Task A_snippet_marks_the_matched_word()
    {
        var test = await Test();
        test.Given(Artwork.Create("Mountain lake", "watercolour of a lake below glaciers"), A);
        await Index(test);

        var hit = (await Ok(test, new SearchQuery("glaciers") { Snippets = true }, A)).Items.ShouldHaveSingleItem();
        hit.Snippet.ShouldNotBeNull();
        hit.Snippet!.ShouldContain("glacier", Case.Insensitive);
        hit.Snippet.ShouldContain("<b>");
    }

    [Fact]
    public async Task Cursor_pages_have_no_repeat_and_a_foreign_cursor_is_refused()
    {
        var test = await Test();
        for (var i = 0; i < 5; i++)
            test.Given(Artwork.Create($"Harbour {i}", "boats"), A);
        await Index(test);

        var seen = new List<Guid>();
        string? after = null;
        var pages = 0;
        do
        {
            var page = await Ok(test, new SearchQuery("harbour") { Limit = 2, After = after }, A);
            seen.AddRange(page.Items.Select(h => h.Id));
            after = page.Next;
            pages++;
        }
        while (after is not null && pages < 10);
        pages.ShouldBe(3);
        seen.Distinct().Count().ShouldBe(5);
        var first = await Ok(test, new SearchQuery("harbour") { Limit = 2 }, A);
        (await test.Find(new SearchQuery("boats") { Limit = 2, After = first.Next }, A)).IsSuccess.ShouldBeFalse();
        (await test.Find(new SearchQuery("harbour") { Limit = 0 }, A)).IsSuccess.ShouldBeFalse();
        (await test.Find(new SearchQuery("harbour") { After = "not-a-cursor" }, A)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task A_change_reindexes_the_aggregate_and_its_entities()
    {
        var test = await Test();
        var art = Artwork.Create("Sunset over harbour", "boats");
        art.AddEdition("museum edition");
        test.Given(art, A);
        await Index(test);
        art.Rename("Dawn over harbour");
        test.Given(art, A);
        await Index(test);

        (await Ok(test, "sunset", A)).Items.ShouldBeEmpty();
        (await Ok(test, "dawn", A)).Items.ShouldHaveSingleItem();
        (await Ok(test, "museum", A)).Items.ShouldHaveSingleItem().Title.ShouldBe("Dawn over harbour");
    }

    [Fact]
    public async Task The_audience_restricts_documents_that_have_one()
    {
        var test = await Test();
        var ada = Artwork.Create("Harbour private", "boats", seller: "ada");
        var open = Artwork.Create("Harbour public", "boats");
        test.Given(ada, A).Given(open, A);
        await Index(test);

        (await Ok(test, new SearchQuery("harbour") { Audience = ["bob"] }, A)).Items.ShouldHaveSingleItem().Id.ShouldBe(open.Id.Value);
        (await Ok(test, new SearchQuery("harbour") { Audience = ["ada", "role:x"] }, A)).Items.Count.ShouldBe(2);
        (await Ok(test, "harbour", A)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Personal_data_is_found_only_by_its_exact_value_and_never_as_text()
    {
        var test = await Test(keys: true);
        var art = Artwork.Create("Portrait", "a portrait", owner: "Ada Lovelace");
        test.Given(art, A);
        await Index(test);

        (await Ok(test, "ada lovelace", A)).Items.ShouldHaveSingleItem().Id.ShouldBe(art.Id.Value);
        (await Ok(test, "  ADA   Lovelace ", A)).Items.ShouldHaveSingleItem();
        (await Ok(test, "lovelace", A)).Items.ShouldBeEmpty();
        (await Ok(test, "ada lovelace", B)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_erased_subject_is_no_longer_found_after_the_index_took_part()
    {
        var test = await Test(keys: true);
        var art = Artwork.Create("Portrait", "a portrait", owner: "Ada Lovelace");
        test.Given(art, A);
        await Index(test);

        await _keys!.EraseSubject(TenantA.Value, art.Id.Value, CancellationToken.None);
        (await Ok(test, "ada lovelace", A)).Items.ShouldHaveSingleItem();     // a tombstone alone does not reach the index
        var streams = await test.Store.StreamsOf(TenantA.Value, art.Id.Value, CancellationToken.None);
        (await test.Host.Reindex(streams, CancellationToken.None)).ShouldBe(1);
        await Settle(test.Index);

        (await Ok(test, "ada lovelace", A)).Items.ShouldBeEmpty();
        (await Ok(test, "portrait", A)).Items.ShouldHaveSingleItem();          // the rest stays searchable
    }

    [Fact]
    public async Task Retention_removes_the_documents_of_deleted_streams_and_export_lists_them_before()
    {
        var test = await Test();
        var art = Artwork.Create("Sunset over harbour", "boats");
        art.AddEdition("museum edition");
        test.Given(art, A);
        await Index(test);

        (await test.Index.Documents(TenantA.Value, [art.Id.Value], CancellationToken.None)).Count.ShouldBe(2);
        await test.Host.Remove([new StreamRef(TenantA.Value, Artwork.StreamType, art.Id.Value)], CancellationToken.None);
        await Settle(test.Index);

        (await Ok(test, "harbour", A)).Items.ShouldBeEmpty();
        (await Ok(test, "museum", A)).Items.ShouldBeEmpty();
        (await test.Index.Documents(TenantA.Value, [art.Id.Value], CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_version_prepared_for_other_members_is_refused()
    {
        var index = await NewIndex();
        var definition = new SearchIndexDefinition(1, "aaaaaaaaaaaaaaaa", [new SearchType("artwork", "Artwork", "artwork")]);
        await index.Prepare(definition, CancellationToken.None);
        await index.Prepare(definition, CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(async () => await index.Prepare(definition with { Fingerprint = "bbbbbbbbbbbbbbbb" }, CancellationToken.None));
    }

    [Fact]
    public async Task The_checkpoint_moves_only_from_where_the_batch_read()
    {
        var test = await Test();
        test.Given(Artwork.Create("Harbour", "boats"), A);
        await Index(test);
        var at = (await test.Index.Checkpoint(CancellationToken.None)).Position;
        at.ShouldNotBe(GlobalPosition.Start);

        (await test.Index.Write(new SearchBatch([], GlobalPosition.Start, at, ProjectionState.Current), CancellationToken.None)).ShouldBeFalse();
        (await test.Index.Checkpoint(CancellationToken.None)).Position.ShouldBe(at);
        test.Host.State.ShouldBe(ProjectionState.Current);
    }
}
