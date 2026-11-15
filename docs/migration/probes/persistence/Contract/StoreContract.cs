using System.Text.Json;
using ArtFlow;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

[assembly: AssemblyFixture(typeof(Probe.Contract.Postgres))]

namespace Probe.Contract;

/// <summary>The store contract (README of Tarol.Persistence), written once. Each test runs in a fresh tenant, so the
/// database needs no cleanup between tests and test classes may run in parallel.</summary>
public abstract class StoreContract
{
    protected static readonly JsonSerializerOptions Json = CreateJson();
    protected readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly Option<Id<ITenant>> _tenant = Id<ITenant>.New();
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static JsonSerializerOptions CreateJson() { var o = ValueObjectJson.Options(AppJson.Default); o.MakeReadOnly(); return o; }

    /// <summary>A store with the stored state, and the unique lookup "lot" of auctions.</summary>
    protected abstract IAggregateStore Store { get; }

    private OperationContext Ctx => OperationContext.Start(Actor.User("alice"), _tenant, Time);
    private OperationContext Other => OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), Time);
#pragma warning disable TVAL006 // test data
    private static Title T => new("Sunflowers");
#pragma warning restore TVAL006
    private Auction NewAuction(string lot) => Auction.Open(Id<Auction>.New(Time), "s", T, lot, 100);

    [Fact]
    public async Task Missing_stream_is_None() => (await Store.Load(Id<Auction>.New(), Ctx, Ct)).IsNone.ShouldBeTrue();

    [Fact]
    public async Task Append_then_load()
    {
        var a = NewAuction("L1");
        _ = a.PlaceBid("bob", 120);
        (await Store.Append(a, 0, Ctx, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Load(a.Id, Ctx, Ct)).TryGetValue(out Auction? loaded).ShouldBeTrue();
        loaded.Version.ShouldBe(2);
        loaded.Bids.Count.ShouldBe(1);
        AggregateState.Write(loaded, Json).ShouldBe(AggregateState.Write(a, Json));
    }

    [Fact]
    public async Task Expected_zero_on_existing_stream_conflicts()
    {
        var a = NewAuction("L1");
        _ = await Store.Append(a, 0, Ctx, Option.None, Ct);
        StoreErrors.IsVersionConflict(await Store.Append(Auction.Open(a.Id, "x", T, "L2", 1), 0, Ctx, Option.None, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Stale_version_conflicts_and_writes_nothing()
    {
        var a = NewAuction("L1");
        _ = await Store.Append(a, 0, Ctx, Option.None, Ct);
        var x = (await Store.Load(a.Id, Ctx, Ct)).TryGetValue(out Auction? l1) ? l1 : null;
        var y = (await Store.Load(a.Id, Ctx, Ct)).TryGetValue(out Auction? l2) ? l2 : null;
        _ = x!.Relot("A"); _ = y!.Relot("B");
        (await Store.Append(x, 1, Ctx, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        StoreErrors.IsVersionConflict(await Store.Append(y, 1, Ctx, Option.None, Ct)).ShouldBeTrue();
        (await Store.Load(a.Id, Ctx, Ct)).TryGetValue(out Auction? now).ShouldBeTrue();
        now.Lot.ShouldBe("A");
    }

    [Fact]
    public async Task Another_tenant_does_not_see_the_stream_or_its_lookups()
    {
        var a = NewAuction("L1");
        _ = await Store.Append(a, 0, Ctx, Option.None, Ct);
        (await Store.Load(a.Id, Other, Ct)).IsNone.ShouldBeTrue();
        (await Store.Find<Auction>("lot", "L1", Other, Ct)).IsNone.ShouldBeTrue();
        (await Store.Find<Auction>("lot", "L1", Ctx, Ct)).TryGetValue(out Id<Auction> found).ShouldBeTrue();
        found.ShouldBe(a.Id);
    }

    [Fact]
    public async Task Key_is_stored_with_the_commit_and_refused_when_stored_meanwhile()
    {
        var key = new IdempotencyKey(_tenant, "payments", "evt_1");
        var a = NewAuction("L1");
        (await Store.Append(a, 0, Ctx, key, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Seen(key, Ct)).TryGetValue(out StoredOutcome outcome).ShouldBeTrue();
        outcome.ShouldBe(new StoredOutcome(a.Id.Value, 1, 1));
        var dup = await Store.Append(NewAuction("L2"), 0, Ctx, key, Ct);
        (dup.TryGetValue(out Violation? v) && v is Error { Code: StoreErrors.DuplicateKeyCode }).ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_commit_stores_no_key()
    {
        var a = NewAuction("L1");
        _ = await Store.Append(a, 0, Ctx, Option.None, Ct);
        var key = new IdempotencyKey(_tenant, "payments", "evt_2");
        StoreErrors.IsVersionConflict(await Store.Append(Auction.Open(a.Id, "x", T, "L9", 1), 0, Ctx, key, Ct)).ShouldBeTrue();
        (await Store.Seen(key, Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task Taken_lookup_refuses_the_commit_and_a_changed_key_moves()
    {
        var a = NewAuction("L1");
        _ = await Store.Append(a, 0, Ctx, Option.None, Ct);
        var b = NewAuction("L1");
        var taken = await Store.Append(b, 0, Ctx, Option.None, Ct);
        (taken.TryGetValue(out Violation? v) && v is Error { Code: StoreErrors.LookupTakenCode }).ShouldBeTrue();
        (await Store.Load(b.Id, Ctx, Ct)).IsNone.ShouldBeTrue();
        var l = (await Store.Load(a.Id, Ctx, Ct)).TryGetValue(out Auction? x) ? x : null;
        _ = l!.Relot("L2");
        (await Store.Append(l, 1, Ctx, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.Find<Auction>("lot", "L1", Ctx, Ct)).IsNone.ShouldBeTrue();
        (await Store.Find<Auction>("lot", "L2", Ctx, Ct)).IsSome.ShouldBeTrue();
    }

    [Fact]
    public async Task Load_by_replay_aggregate_round_trips()
    {
        var l = Ledger.Start(Id<Ledger>.New());
        _ = l.Book(5); _ = l.Book(7);
        _ = await Store.Append(l, 0, Ctx, Option.None, Ct);
        (await Store.Load(l.Id, Ctx, Ct)).TryGetValue(out Ledger? loaded).ShouldBeTrue();
        loaded.Total.ShouldBe(12);
    }

    [Fact]
    public async Task Sixteen_concurrent_appends_to_one_stream_commit_exactly_one()
    {
        var a = NewAuction("L1");
        _ = await Store.Append(a, 0, Ctx, Option.None, Ct);
        var loaded = new List<Auction>();
        for (var i = 0; i < 16; i++)
        {
            var x = (await Store.Load(a.Id, Ctx, Ct)).TryGetValue(out Auction? l) ? l : null;
            _ = x!.Relot($"R{i}");
            loaded.Add(x);
        }
        var results = await Task.WhenAll(loaded.Select(x => Store.Append(x, 1, Ctx, Option.None, Ct).AsTask()));
        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Count(StoreErrors.IsVersionConflict).ShouldBe(15);
    }
}

public sealed class InMemoryStoreContract : StoreContract
{
    protected override IAggregateStore Store { get; }

    public InMemoryStoreContract() => Store = new InMemoryAggregateStore(Time, Json).DefineLookup<Auction>("lot", a => a.Lot);
}

public sealed class NpgsqlStoreContract(Postgres postgres) : StoreContract
{
    protected override IAggregateStore Store { get; } = postgres.Store(TimeProvider.System);
}

/// <summary>One container per test run (assembly fixture); the schema is created once.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private NpgsqlDataSource? _db;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _db = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString()).EnableArrays().Build();
        await Store(TimeProvider.System).EnsureSchema(default);
    }

    public NpgsqlAggregateStore Store(TimeProvider time)
    {
        var json = ValueObjectJson.Options(AppJson.Default);
        var codec = new EventCodec(json).Add<Auction>(Catalogs.Auction).Add<Ledger>(Catalogs.Ledger);
        return new NpgsqlAggregateStore(_db!, codec, time, new NpgsqlStoreOptions
        {
            StateOptions = codec.Options,
            Lookups = [new KeyLookup("auction", "lot", a => ((Auction)a).Lot)],
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
        await _container.DisposeAsync();
    }
}
