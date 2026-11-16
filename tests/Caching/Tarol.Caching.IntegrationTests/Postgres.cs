using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Queries.Npgsql;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Caching.IntegrationTests.Postgres))]

namespace Tarol.Caching.IntegrationTests;

public partial class Auction : AggregateRoot<Auction>
{
    public decimal HighestBid { get; private set; }

    public static Auction Open(string title, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var auction = new Auction(Id<Auction>.New(now));
        auction.Raise(new Opened(title, endsAt));
        return auction;
    }

    public Result Bid(decimal amount) => amount <= HighestBid ? Error.Conflict("Auction.BidTooLow") : Raise(new BidPlaced(amount));

    public sealed record Opened(string Title, DateTimeOffset EndsAt) : Event;

    public sealed record BidPlaced(decimal Amount) : Event;

#pragma warning disable CA1822 // the title is only projected
    private void On(Opened e) { }
#pragma warning restore CA1822

    private void On(BidPlaced e) => HighestBid = e.Amount;
}

[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, [Sort] DateTimeOffset EndsAt, decimal HighestBid)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.EndsAt, 0m);

    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Auction.Opened))]
[JsonSerializable(typeof(Auction.BidPlaced))]
[JsonSerializable(typeof(AuctionRow))]
public sealed partial class TestJson : JsonSerializerContext;

/// <summary>One PostgreSQL 17 container per run; every test gets a schema of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    public static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public static readonly JsonSerializerOptions Json = new(TestJson.Default.Options);

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString() + ";Maximum Pool Size=50").EnableArrays().Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>The worker (store, pushing read tables, host) and the API process (pushed positions, cached read stores) on a fresh schema.</summary>
    public async Task<Lab> Create()
    {
        var schema = $"c{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";
        var store = new NpgsqlAggregateStore(DataSource, new EventCodec(Json).Add<Auction>(), TimeProvider.System, new NpgsqlStoreOptions { Schema = schema });
        await store.EnsureSchema(default);
        var readModels = new NpgsqlReadModels(DataSource, Json, schema, pushCheckpoints: true);
        var positions = new NpgsqlProjectionPositions(DataSource, schema);
        var cached = new CachedReadModels(new NpgsqlReadModels(DataSource, Json, schema), positions, new MemoryPageCache(), Json);
        return new Lab(store, readModels, new ProjectionHost<AuctionRow>(store, readModels.Store<AuctionRow>()), positions, cached);
    }
}

public sealed record Lab(NpgsqlAggregateStore Store, NpgsqlReadModels Worker, ProjectionHost<AuctionRow> Host, NpgsqlProjectionPositions Positions,
    CachedReadModels Api) : IAsyncDisposable
{
    public static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000a");
    public static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The cached store of the API process.</summary>
    public IReadModelStore<AuctionRow> Cached => Api.Store<AuctionRow>();

    /// <summary>The same table without the cache.</summary>
    public IReadModelStore<AuctionRow> Uncached => Api.Inner.Store<AuctionRow>();

    public async Task<GlobalPosition> Commit(Auction auction, Id<ITenant> tenant)
    {
        var context = new OperationContext(Actor.User("bidder"), tenant, Id<ICorrelation>.New(Postgres.Now), Option.None);
        var appended = await Store.Append(auction, auction.Version - auction.UncommittedChanges.Length, context, Option.None, Ct);
        appended.TryGetValue(out GlobalPosition position).ShouldBeTrue(appended.ToString());
        AggregateChanges.Committed(auction);
        return position;
    }

    /// <summary>Runs the worker until it passed <paramref name="position"/>, then waits for the API process to see it pushed.</summary>
    public async Task Project(GlobalPosition position)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Host.Position < position)
        {
            await Host.CatchUp(Ct);
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), $"the host is stuck at {Host.Position}, waiting for {position}");
            if (Host.Position < position)
                await Task.Delay(20, Ct);
        }
        (await Positions.WaitFor<AuctionRow>(Host.Position, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
    }

    public static async Task<Page<AuctionRow>> Page(IReadModelStore<AuctionRow> store, ListQuery query)
    {
        var result = await store.Search(query, Ct);
        result.TryGetValue(out Page<AuctionRow>? page).ShouldBeTrue(result.ToString());
        return page!;
    }

    public ValueTask DisposeAsync() => Positions.DisposeAsync();
}
