using Tarol.Kernel;
using Tarol.Queries;
using Tarol.Search.Contract;
using Tarol.Search.Npgsql;
using Tarol.StronglyTypedId;

namespace Tarol.Search.OpenSearch.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OpenSearchTests(Engine engine)
{
    private static readonly Id<ITenant> Tenant = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-0000000000dd");

    private OpenSearchIndex Index(int? n = null, OpenSearchOptions? options = null)
    {
        n ??= engine.Next();
        return new OpenSearchIndex(engine.Client(), new NpgsqlSearchCheckpoints(engine.DataSource, $"ost{n}_{Environment.ProcessId}"),
            (options ?? new OpenSearchOptions()) with { IndexPrefix = $"t-{n}-{Environment.ProcessId}", Replicas = 0 });
    }

    [Fact]
    public async Task German_and_english_documents_stem_and_fold_accents()
    {
        var test = SearchTest.Create(s => s.Add<Artwork>(), Index(options: new OpenSearchOptions { WaitForRefresh = true }));
        var de = Artwork.Create("Café Müller", "Gemälde in Öl, Häuser am Fluss", language: "de");
        var en = Artwork.Create("Cafe Society", "paintings of houses by the river", language: "en");
        test.Given(de, Tenant).Given(en, Tenant);
        await test.CatchUp();

        async Task<Guid[]> Find(string text, string? language)
            => [.. (await test.Find(new SearchQuery(text) { Language = language }, Tenant)).GetValueOrThrow().Items.Select(h => h.Id)];
        (await Find("Häuser", "de")).ShouldContain(de.Id.Value);
        (await Find("gemalde", "de")).ShouldBe([de.Id.Value]);
        (await Find("house", "en")).ShouldBe([en.Id.Value]);
        (await Find("cafe", null)).Order().ShouldBe(new[] { de.Id.Value, en.Id.Value }.Order());
    }

    [Fact]
    public async Task The_checkpoint_lives_in_postgres_and_a_new_version_builds_while_the_old_one_answers()
    {
        var n = engine.Next();
        var v1 = SearchTest.Create(s => s.Add<Artwork>(), Index(n, new OpenSearchOptions { WaitForRefresh = true }));
        var art = Artwork.Create("Sunset over harbour", "boats");
        v1.Given(art, Tenant);
        await v1.CatchUp();
        (await engine.DataSource.CreateCommand($"SELECT state FROM ost{n}_{Environment.ProcessId}.projections WHERE name = 'search' AND version = 1")
            .ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("current");

        var v2 = Index(n);
        var registry = new SearchRegistry { Version = 2 }.Add<Artwork>();
        await v2.Prepare(registry.Definition(), TestContext.Current.CancellationToken);
        var page = (await v2.Search(new SearchRequest(Tenant.Value, "harbour"), TestContext.Current.CancellationToken)).GetValueOrThrow();

        page.Version.ShouldBe(1);
        page.State.ShouldBe(ProjectionState.Current);
        page.Items.ShouldHaveSingleItem().Id.ShouldBe(art.Id.Value);
    }

    [Fact]
    public async Task An_unreachable_cluster_is_a_readiness_problem_until_it_answers()
    {
        using var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(5) };
        var index = new OpenSearchIndex(dead, new InMemorySearchCheckpoints());
        index.Problem.ShouldBeNull();

        await Should.ThrowAsync<HttpRequestException>(async () => await index.Prepare(new SearchRegistry().Add<Artwork>().Definition(), TestContext.Current.CancellationToken));

        index.Problem.ShouldNotBeNull().ShouldContain("did not answer");
        Index().Problem.ShouldBeNull();
    }
}
