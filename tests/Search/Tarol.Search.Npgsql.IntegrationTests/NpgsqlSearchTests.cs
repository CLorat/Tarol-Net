using System.Security.Cryptography;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Queries;
using Tarol.Search.Contract;
using Tarol.StronglyTypedId;

namespace Tarol.Search.Npgsql.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class NpgsqlSearchTests(Postgres postgres)
{
    private static readonly Id<ITenant> Tenant = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-0000000000cc");

    private static OperationContext Context => OperationContext.Start(Actor.User("u"), Tenant, TimeProvider.System);

    [Fact]
    public async Task German_and_english_documents_stem_in_their_language_and_fold_accents()
    {
        var test = SearchTest.Create(s => s.Add<Artwork>(), new NpgsqlSearchIndex(postgres.DataSource, postgres.Schema()));
        var de = Artwork.Create("Café Müller", "Gemälde in Öl, Häuser am Fluss", language: "de");
        var en = Artwork.Create("Cafe Society", "paintings of houses by the river", language: "en");
        test.Given(de, Tenant).Given(en, Tenant);
        await test.CatchUp();

        (await Find(test, "Häusern", "de")).ShouldBe([de.Id.Value]);
        (await Find(test, "gemalde", "de")).ShouldBe([de.Id.Value]);
        (await Find(test, "house", "en")).ShouldBe([en.Id.Value]);
        (await Find(test, "cafe", null)).Order().ShouldBe(new[] { de.Id.Value, en.Id.Value }.Order());
    }

    private static async Task<Guid[]> Find(SearchTest test, string text, string? language)
        => [.. (await test.Find(new SearchQuery(text) { Language = language }, Tenant)).GetValueOrThrow().Items.Select(h => h.Id)];

    [Fact]
    public async Task The_previous_version_answers_while_a_new_one_builds()
    {
        var schema = postgres.Schema();
        var v1 = SearchTest.Create(s => s.Add<Artwork>(), new NpgsqlSearchIndex(postgres.DataSource, schema));
        var art = Artwork.Create("Sunset over harbour", "boats");
        v1.Given(art, Tenant);
        await v1.CatchUp();

        var index2 = new NpgsqlSearchIndex(postgres.DataSource, schema);
        var registry = new SearchRegistry { Version = 2 }.Add<Artwork>();
        await index2.Prepare(registry.Definition(), CancellationToken.None);
        var page = (await index2.Search(new SearchRequest(Tenant.Value, "harbour") { Types = [Artwork.StreamType] }, CancellationToken.None)).GetValueOrThrow();

        page.Version.ShouldBe(1);
        page.State.ShouldBe(ProjectionState.Current);
        page.Items.ShouldHaveSingleItem().Id.ShouldBe(art.Id.Value);
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.projections WHERE name = 'search'")).ShouldBe(2L);
    }

    [Fact]
    public async Task Without_a_current_version_the_building_one_answers_and_says_so()
    {
        var index = new NpgsqlSearchIndex(postgres.DataSource, postgres.Schema());
        await index.Prepare(new SearchRegistry().Add<Artwork>().Definition(), CancellationToken.None);

        var page = (await index.Search(new SearchRequest(Tenant.Value, "harbour"), CancellationToken.None)).GetValueOrThrow();

        page.State.ShouldBe(ProjectionState.Building);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_extensions_are_created_and_no_personal_text_is_stored()
    {
        var schema = postgres.Schema();
        var keys = new InMemoryKeyStore(new ConfiguredKeyEncryptionKey("t", RandomNumberGenerator.GetBytes(32)), TimeProvider.System);
        var test = SearchTest.Create(s => s.Add<Artwork>(), new NpgsqlSearchIndex(postgres.DataSource, schema), keys, ContractJson.Codec());
        test.Given(Artwork.Create("Portrait", "a portrait", owner: "Ada Lovelace"), Tenant);
        await test.CatchUp();

        (await postgres.Scalar("SELECT count(*) FROM pg_extension WHERE extname IN ('pg_trgm', 'btree_gin', 'unaccent')")).ShouldBe(3L);
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.search_v1 WHERE concat_ws(' ', title, a, b, c, d, array_to_string(exact, ' ')) ILIKE '%lovelace%'")).ShouldBe(0L);
        (await postgres.Scalar($"SELECT cardinality(exact) FROM {schema}.search_v1")).ShouldBe(1);
    }

    [Fact]
    public async Task Fed_from_the_npgsql_store_erasure_and_retention_reach_the_index()
    {
        var schema = postgres.Schema();
        await using var keys = new NpgsqlKeyStore(postgres.DataSource, new ConfiguredKeyEncryptionKey("t", RandomNumberGenerator.GetBytes(32)), TimeProvider.System,
            new NpgsqlKeyStoreOptions { Schema = schema });
        var store = new NpgsqlAggregateStore(postgres.DataSource, ContractJson.Codec(), TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        await store.EnsureSchema(SchemaMode.Upgrade, CancellationToken.None);
        var index = new NpgsqlSearchIndex(postgres.DataSource, schema);
        var registry = new SearchRegistry().Add<Artwork>();
        var host = new SearchHost(registry, index, store, store, new SearchTokens(keys));
        var search = new SearchService(registry, index, new SearchTokens(keys));
        var art = Artwork.Create("Portrait", "a portrait", owner: "Ada Lovelace");
        art.AddEdition("museum edition");
        var position = (await store.Append(art, 0, Context, Option.None, CancellationToken.None)).GetValueOrThrow();
        Tarol.Primitives.AggregateChanges.Committed(art);
        (await store.Append(Note.Write("portrait note"), 0, Context, Option.None, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        while (host.Position < position)
            await host.CatchUp(CancellationToken.None);
        (await search.Search(new SearchQuery("ada lovelace"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldHaveSingleItem();
        (await search.Search(new SearchQuery("museum"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldHaveSingleItem().Aggregate.ShouldBe(art.Id.Value);
        (await search.Search(new SearchQuery("note"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldBeEmpty();

        (await keys.EraseSubject(Tenant.Value, art.Id.Value, CancellationToken.None)).ShouldBeTrue();
        (await host.Reindex(await store.StreamsOf(Tenant.Value, art.Id.Value, CancellationToken.None), CancellationToken.None)).ShouldBe(1);
        (await search.Search(new SearchQuery("ada lovelace"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldBeEmpty();
        (await search.Search(new SearchQuery("portrait"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldHaveSingleItem();

        var deleted = await store.DeleteStreams(Artwork.StreamType, TimeProvider.System.GetUtcNow().AddDays(1), 500, CancellationToken.None);
        await host.Remove(deleted, CancellationToken.None);
        (await search.Search(new SearchQuery("museum"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldBeEmpty();
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.search_v1")).ShouldBe(0L);
    }

    [Fact]
    public async Task An_erasure_in_a_process_whose_host_never_ran_reaches_the_index()   // the API process: its host stays gated
    {
        var schema = postgres.Schema();
        await using var keys = new NpgsqlKeyStore(postgres.DataSource, new ConfiguredKeyEncryptionKey("t", RandomNumberGenerator.GetBytes(32)), TimeProvider.System,
            new NpgsqlKeyStoreOptions { Schema = schema });
        var store = new NpgsqlAggregateStore(postgres.DataSource, ContractJson.Codec(), TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        await store.EnsureSchema(SchemaMode.Upgrade, CancellationToken.None);
        var registry = new SearchRegistry().Add<Artwork>();
        var worker = new SearchHost(registry, new NpgsqlSearchIndex(postgres.DataSource, schema), store, store, new SearchTokens(keys));
        var apiIndex = new NpgsqlSearchIndex(postgres.DataSource, schema);
        var api = new SearchHost(registry, apiIndex, store, store, new SearchTokens(keys));      // never started, never caught up
        var search = new SearchService(registry, apiIndex, new SearchTokens(keys));
        var art = Artwork.Create("Portrait", "a portrait", owner: "Ada Lovelace");
        var position = (await store.Append(art, 0, Context, Option.None, CancellationToken.None)).GetValueOrThrow();
        Tarol.Primitives.AggregateChanges.Committed(art);
        while (worker.Position < position)
            await worker.CatchUp(CancellationToken.None);
        (await search.Search(new SearchQuery("ada lovelace"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldHaveSingleItem();

        (await keys.EraseSubject(Tenant.Value, art.Id.Value, CancellationToken.None)).ShouldBeTrue();
        (await api.Reindex(await store.StreamsOf(Tenant.Value, art.Id.Value, CancellationToken.None), CancellationToken.None)).ShouldBe(1);

        (await search.Search(new SearchQuery("ada lovelace"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldBeEmpty();
        (await search.Search(new SearchQuery("portrait"), Context, CancellationToken.None)).GetValueOrThrow().Items.ShouldHaveSingleItem();
        await api.Remove(await store.StreamsOf(Tenant.Value, art.Id.Value, CancellationToken.None), CancellationToken.None);
        (await postgres.Scalar($"SELECT count(*) FROM {schema}.search_v1")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_missing_extension_privilege_is_explained()
    {
        // a database of its own: its owner is the superuser, the app's role may connect and owns a schema, but may not create extensions
        var schema = postgres.Schema();
        var (role, database) = ("r" + schema, "d" + schema);
        await postgres.Execute($"CREATE ROLE {role} LOGIN PASSWORD 'x'");
        await postgres.Execute($"CREATE DATABASE {database}");
        var admin = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database };
        await using (var setup = new NpgsqlSlimDataSourceBuilder(admin.ConnectionString).Build())
        await using (var command = setup.CreateCommand($"REVOKE CREATE ON DATABASE {database} FROM PUBLIC; CREATE SCHEMA {schema} AUTHORIZATION {role}"))
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var app = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = role, Password = "x" };
        await using var restricted = new NpgsqlSlimDataSourceBuilder(app.ConnectionString).EnableArrays().Build();

        var index = new NpgsqlSearchIndex(restricted, schema);
        var error = await Should.ThrowAsync<InvalidOperationException>(async () => await index.Prepare(new SearchRegistry().Add<Artwork>().Definition(), CancellationToken.None));

        error.Message.ShouldContain("pg_trgm, btree_gin and unaccent");
    }

    [Fact]
    public async Task Indexes_in_different_schemas_prepare_at_once_in_a_fresh_database()
    {
        // CREATE EXTENSION IF NOT EXISTS from two transactions at once fails one of them (23505 on pg_extension) without a lock
        var database = "x" + postgres.Schema();
        await postgres.Execute($"CREATE DATABASE {database}");
        var fresh = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database };
        await using var db = new NpgsqlSlimDataSourceBuilder(fresh.ConnectionString).EnableArrays().Build();
        var definition = new SearchRegistry().Add<Artwork>().Definition();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => new NpgsqlSearchIndex(db, $"p{i}").Prepare(definition, TestContext.Current.CancellationToken).AsTask()));

        await using var count = db.CreateCommand("SELECT count(*) FROM pg_extension WHERE extname IN ('pg_trgm', 'btree_gin', 'unaccent')");
        (await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(3L);
    }
}

