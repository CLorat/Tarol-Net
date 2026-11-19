using Tarol.Kernel;
using Tarol.Queries;
using Tarol.Search.Contract;
using Tarol.Search.Npgsql;
using Tarol.StronglyTypedId;

namespace Tarol.Search.Typesense.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class TypesenseTests(Engine engine)
{
    private static readonly Id<ITenant> Tenant = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-0000000000ee");

    private TypesenseIndex Index(int? n = null)
    {
        n ??= engine.Next();
        return new TypesenseIndex(engine.Client(), new NpgsqlSearchCheckpoints(engine.DataSource, $"tst{n}_{Environment.ProcessId}"),
            new TypesenseOptions { CollectionPrefix = $"t_{n}_{Environment.ProcessId}" });
    }

    [Fact]
    public async Task German_and_english_documents_stem_and_fold_accents()
    {
        var test = SearchTest.Create(s => s.Add<Artwork>(), Index());
        var de = Artwork.Create("Café Müller", "Gemälde in Öl, Häuser am Fluss", language: "de");
        var en = Artwork.Create("Cafe Society", "paintings of houses by the river", language: "en");
        test.Given(de, Tenant).Given(en, Tenant);
        await test.CatchUp();

        async Task<Guid[]> Find(string text, string? language)
            => [.. (await test.Find(new SearchQuery(text) { Language = language }, Tenant)).GetValueOrThrow().Items.Select(h => h.Id)];
        (await Find("gemalde", "de")).ShouldBe([de.Id.Value]);
        (await Find("house", "en")).ShouldBe([en.Id.Value]);
        (await Find("cafe", null)).Order().ShouldBe(new[] { de.Id.Value, en.Id.Value }.Order());
    }

    [Fact]
    public async Task A_new_version_builds_while_the_old_one_answers()
    {
        var n = engine.Next();
        var v1 = SearchTest.Create(s => s.Add<Artwork>(), Index(n));
        var art = Artwork.Create("Sunset over harbour", "boats");
        v1.Given(art, Tenant);
        await v1.CatchUp();

        var v2 = Index(n);
        await v2.Prepare(new SearchRegistry { Version = 2 }.Add<Artwork>().Definition(), TestContext.Current.CancellationToken);
        var page = (await v2.Search(new SearchRequest(Tenant.Value, "harbour"), TestContext.Current.CancellationToken)).GetValueOrThrow();

        page.Version.ShouldBe(1);
        page.State.ShouldBe(ProjectionState.Current);
        page.Items.ShouldHaveSingleItem().Id.ShouldBe(art.Id.Value);
    }

    [Fact]
    public async Task An_unreachable_server_is_a_readiness_problem()
    {
        using var dead = TypesenseTarolBuilderExtensions.Client(new Uri("http://127.0.0.1:1"), "throwaway");
        var index = new TypesenseIndex(dead, new InMemorySearchCheckpoints());

        await Should.ThrowAsync<HttpRequestException>(async () => await index.Prepare(new SearchRegistry().Add<Artwork>().Definition(), TestContext.Current.CancellationToken));

        index.Problem.ShouldNotBeNull().ShouldContain("did not answer");
        Index().Problem.ShouldBeNull();
    }
}
