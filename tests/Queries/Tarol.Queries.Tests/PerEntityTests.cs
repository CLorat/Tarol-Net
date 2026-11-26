using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using static Tarol.Queries.Tests.Fixtures;

namespace Tarol.Queries.Tests;

/// <summary>A kennel and its dogs: the dogs are entities of the kennel's stream (TN-54, P-1).</summary>
public partial class Kennel : AggregateRoot<Kennel>
{
    public EntitySet<Dog> Dogs { get; } = [];
    public string Name { get; private set; } = "";

    public static Kennel Open(string name)
    {
        var kennel = new Kennel(Id<Kennel>.New(Now));
        kennel.Raise(new Opened(name));
        return kennel;
    }

    public Dog Add(string name) => Raise(new Dog.Added(name));

    public Result Rename(string name) => Raise(new Renamed(name));

    public sealed record Opened(string Name) : Event;

    public sealed record Renamed(string Name) : Event;

    private void On(Opened e) => Name = e.Name;

    private void On(Renamed e) => Name = e.Name;
}

public partial class Dog : Entity<Kennel, Dog>
{
    public string Name { get; private set; } = "";

    public Success Rename(string name) => Raise(new Renamed(name));

    public Success Leave() => Raise(new Left());

    public sealed record Added(string Name) : Created;

    public sealed record Renamed(string Name) : Event;

    public sealed record Left : Deleted;

    private void On(Added e) => Name = e.Name;

    private void On(Renamed e) => Name = e.Name;
}

/// <summary>One row per dog: keyed by the dog's id, the kennel's stream kept for erasure and retention.</summary>
[Projection("dog-list", PerEntity = true)]
public sealed partial record DogRow(Guid Id, Guid Kennel, string Name, bool Gone)
{
    public static DogRow When(Dog.Added e, RecordedEvent r) => new(r.Event.EntityId, r.StreamId, e.Name, false);

    public DogRow When(Dog.Renamed e) => this with { Name = e.Name };

    public DogRow When(Dog.Left e) => this with { Gone = true };
}

/// <summary>Rows per entity (TN-54, owner decision P-1).</summary>
public sealed class PerEntityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool PerEntityOf<T>()
        where T : class, IProjection<T>
        => T.PerEntity;

    [Fact]
    public async Task Each_entity_is_a_row_keyed_by_its_id_and_root_events_are_not_applied()
    {
        var test = ProjectionTest<DogRow>.Create();
        var kennel = Kennel.Open("North");
        var rex = kennel.Add("Rex");
        var bello = kennel.Add("Bello");
        test.Given(kennel, A);
        rex.Rename("Rexy");
        kennel.Rename("North Kennel").ShouldBe(Result.Success());
        bello.Leave();
        test.Given(kennel, A);
        await test.CatchUp();

        var rows = test.ReadModels.Rows.OrderBy(r => r.Row.Name, StringComparer.Ordinal).ToList();

        PerEntityOf<DogRow>().ShouldBeTrue();
        PerEntityOf<AuctionRow>().ShouldBeFalse();
        rows.Select(r => (r.Key.Id, r.Row.Name, r.Row.Gone)).ShouldBe([(bello.Id.Value, "Bello", true), (rex.Id.Value, "Rexy", false)]);
        rows.ShouldAllBe(r => r.Stream == kennel.Id.Value && r.Key.Tenant == TenantA.Value && r.Row.Kennel == kennel.Id.Value);
        (await test.Search(Query(A) with { Count = true })).GetValueOrThrow().Total.ShouldBe(2);
    }

    [Fact]
    public async Task Rows_are_removed_by_their_stream_and_a_row_per_stream_keeps_removing_by_key()
    {
        var test = ProjectionTest<DogRow>.Create();
        var north = Kennel.Open("North");
        north.Add("Rex");
        north.Add("Bello");
        var south = Kennel.Open("South");
        var luna = south.Add("Luna");
        test.Given(north, A).Given(south, A);
        await test.CatchUp();

        var removed = await test.ReadModels.RemoveStreams([new RowKey(TenantA.Value, north.Id.Value)], Ct);

        removed.ShouldBe(2);
        test.ReadModels.Rows.Select(r => r.Key.Id).ShouldBe([luna.Id.Value]);
        var auctions = ProjectionTest<AuctionRow>.Create();
        var auction = Seed(auctions, 1, A)[0];
        await auctions.CatchUp();
        (await auctions.ReadModels.RemoveStreams([new RowKey(TenantA.Value, auction.Id.Value)], Ct)).ShouldBe(1);   // the default: by key
    }
}
