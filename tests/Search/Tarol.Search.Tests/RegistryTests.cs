using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Search.Contract;
using Tarol.Testing.Metrics;

namespace Tarol.Search.Tests;

public sealed class RegistryTests
{
    [Fact]
    public void The_generated_shape_names_the_aggregate_and_its_entities()
    {
        var shape = SearchDocumentsOf<Artwork>.Shape;

        shape.StreamType.ShouldBe(Artwork.StreamType);
        shape.Types.Select(t => (t.Type, t.Label, t.Aggregate)).ShouldBe([
            (Artwork.StreamType, "Artwork", Artwork.StreamType),
            (Artwork.StreamType + ".edition", "Edition", Artwork.StreamType)]);
        shape.Fingerprint.Length.ShouldBe(16);
    }

    [Fact]
    public void The_definition_changes_with_the_registered_shapes_and_not_with_their_order()
    {
        var one = new SearchRegistry().Add<Artwork>().Add<Artist>().Definition();
        var other = new SearchRegistry().Add<Artist>().Add<Artwork>().Definition();
        var less = new SearchRegistry().Add<Artwork>().Definition();

        other.Fingerprint.ShouldBe(one.Fingerprint);
        less.Fingerprint.ShouldNotBe(one.Fingerprint);
        one.Types.Count.ShouldBe(3);
    }

    [Fact]
    public void An_aggregate_added_twice_is_refused()
        => Should.Throw<InvalidOperationException>(() => new SearchRegistry().Add<Artwork>().Add<Artwork>());

    [Fact]
    public void The_documents_carry_audience_language_and_exact_values()
    {
        var art = Artwork.Create("Sunset", "boats", owner: "Ada", seller: "ada", language: "de");
        art.AddEdition("print");
        var documents = new List<SearchDocument>();
        SearchDocumentsOf<Artwork>.Documents(art, documents);

        documents.Count.ShouldBe(2);
        documents[0].A.ShouldBe("Sunset");
        documents[0].B.ShouldBe("boats");
        documents[0].Exact.ShouldBe(["Ada"]);
        documents[0].Audience.ShouldBe(["ada"]);
        documents[0].Language.ShouldBe("de");
        documents[1].Type.ShouldBe(Artwork.StreamType + ".edition");
        documents[1].Title.ShouldBe("Sunset");
        documents[1].Audience.ShouldBe(["ada"]);
        documents[1].Exact.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddSearch_registers_the_host_the_search_and_the_eraser()
    {
        var services = new ServiceCollection();
        var builder = new TarolBuilder(services, new System.Text.Json.JsonSerializerOptions(ContractJson.Default.Options), ContractJson.Codec())
            .UseInMemoryStore()
            .AddSearch(s => { s.Add<Artwork>(); s.Version = 2; })
            .UseInMemorySearch();
        using var meters = new Measurements();
        services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory>(meters);
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<SearchHost>();
        host.Version.ShouldBe(2);
        host.Name.ShouldBe("search");
        provider.GetServices<IHostedService>().ShouldContain(host);
        provider.GetServices<ProjectionHost>().ShouldContain(host);
        provider.GetServices<ErasureParticipant>().OfType<SearchEraser>().ShouldHaveSingleItem();
        var store = provider.GetRequiredService<IAggregateStore>();
        var art = Artwork.Create("Harbour", "boats");
        (await store.Append(art, 0, Tarol.Kernel.OperationContext.Start(Tarol.Kernel.Actor.User("u"), Tarol.Options.Option.None, TimeProvider.System),
            Tarol.Options.Option.None, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        await host.CatchUp(CancellationToken.None);
        var search = provider.GetRequiredService<ISearch>();
        var page = (await search.Search(new SearchQuery("harbour"), Tarol.Kernel.OperationContext.Start(Tarol.Kernel.Actor.User("u"), Tarol.Options.Option.None, TimeProvider.System),
            CancellationToken.None)).GetValueOrThrow();
        page.Items.ShouldHaveSingleItem();
        meters.Of("tarol.search.queries").ShouldHaveSingleItem().Tags["tarol.outcome"].ShouldBe("ok");
        meters.Of("tarol.search.documents").Sum(m => m.Value).ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => builder.AddSearch(s => s.Add<Artist>()));
    }

    [Fact]
    public void AddSearch_without_an_index_says_how_to_add_one()
    {
        var services = new ServiceCollection();
        _ = new TarolBuilder(services, new System.Text.Json.JsonSerializerOptions(ContractJson.Default.Options), ContractJson.Codec())
            .UseInMemoryStore()
            .AddSearch(s => s.Add<Artwork>());
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<SearchHost>()).Message.ShouldContain("UseInMemorySearch");
    }

    /// <summary>Calls the explicit static members the generator implements.</summary>
    private static class SearchDocumentsOf<T>
        where T : Tarol.Primitives.AggregateRoot<T>, Tarol.Primitives.IAggregateRoot<T>, ISearchable<T>
    {
        public static SearchShape Shape => T.Shape;

        public static void Documents(T aggregate, ICollection<SearchDocument> into) => T.Documents(aggregate, into);
    }
}
