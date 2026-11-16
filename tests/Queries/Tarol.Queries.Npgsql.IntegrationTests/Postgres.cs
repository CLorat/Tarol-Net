using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.StronglyTypedId;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Queries.Npgsql.IntegrationTests.Postgres))]

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>One PostgreSQL 17 container per run; every test gets a schema of its own (store and read tables together).</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    public static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public static readonly JsonSerializerOptions Json = new(TestJson.Default.Options);

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString() + ";Maximum Pool Size=100").EnableArrays().Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>A store and its read models on a fresh schema.</summary>
    public async Task<Setup> Create(bool notifyFeed = false, bool pushCheckpoints = false)
    {
        var schema = $"q{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";
        var codec = new EventCodec(Json).Add<Auction>().Add<Lot>();
        var store = new NpgsqlAggregateStore(DataSource, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = schema, NotifyFeed = notifyFeed });
        await store.EnsureSchema(default);
        return new Setup(this, store, new NpgsqlReadModels(DataSource, Json, schema, pushCheckpoints));
    }

    public async Task<object?> Scalar(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    public async Task Execute(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record Setup(Postgres Postgres, NpgsqlAggregateStore Store, NpgsqlReadModels ReadModels)
{
    public static readonly Id<ITenant> TenantA = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000a");
    public static readonly Id<ITenant> TenantB = (Id<ITenant>)Guid.Parse("0196f000-0000-7000-8000-00000000000b");

    private GlobalPosition _last;

    public string Schema => Store.Schema;

    /// <summary>The position of the last commit made through <see cref="Commit"/>.</summary>
    public GlobalPosition Last => _last;

    /// <summary>
    /// Runs the host until it has passed the last commit. A plain CatchUp can stop early: another test's open transaction
    /// hides later commits from the feed (the xmin horizon, persistence.md §9), so the head is not a fixed point.
    /// </summary>
    public async Task CatchUp(ProjectionHost host)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            await host.CatchUp(TestContext.Current.CancellationToken);
            if (host.Position >= _last)
                return;
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), $"{host.Name} stuck at {host.Position}, last commit {_last}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public static OperationContext Context(Option<Id<ITenant>> tenant) => new(Actor.User("bidder"), tenant, Id<ICorrelation>.New(Postgres.Now), Option.None);

    public ProjectionHost<TRow> Host<TRow>(ProjectionOptions? options = null, IFeedSignal? signal = null)
        where TRow : class, IProjection<TRow>
        => new(Store, ReadModels.Store<TRow>(), options, signal: signal);

    /// <summary>Commits the aggregate's uncommitted events; returns the commit's position.</summary>
    public async Task<GlobalPosition> Commit(Auction auction, Option<Id<ITenant>> tenant)
    {
        var expected = auction.Version - auction.UncommittedChanges.Length;
        var appended = await Store.Append(auction, expected, Context(tenant), Option.None, default);
        appended.TryGetValue(out GlobalPosition position).ShouldBeTrue(appended.ToString());
        lock (Store)
            if (position > _last)
                _last = position;
        Tarol.Primitives.AggregateChanges.Committed(auction);
        return position;
    }

    /// <summary>Opens <paramref name="count"/> auctions ending one hour apart, i % 4 bids each, every third closed; one commit each.</summary>
    public async Task<List<Auction>> Seed(int count, Option<Id<ITenant>> tenant)
    {
        var auctions = new List<Auction>(count);
        for (var i = 0; i < count; i++)
        {
            var auction = Auction.Open(i % 2 == 0 ? "ada" : "bob", $"Lot {i}", Postgres.Now.AddHours(i + 1), Postgres.Now.AddSeconds(i));
            for (var b = 1; b <= i % 4; b++)
                auction.Bid("carla", 10m * b).IsSuccess.ShouldBeTrue();
            if (i % 3 == 0)
                auction.Close().IsSuccess.ShouldBeTrue();
            await Commit(auction, tenant);
            auctions.Add(auction);
        }
        return auctions;
    }

    public async Task<GlobalPosition> Head()
    {
        var head = GlobalPosition.Start;
        for (var after = GlobalPosition.Start; ;)
        {
            var any = false;
            await foreach (var e in Store.ReadAll(after, 5000, default))
            {
                head = after = e.Position;
                any = true;
            }
            if (!any)
                return head;
        }
    }

    public static ListQuery Query(Id<ITenant> tenant) => new(tenant.Value);
}

internal static class PageExtensions
{
    public static Page<T> Ok<T>(this Tarol.Results.Result<Page<T>> result)
        where T : class
    {
        result.TryGetValue(out Page<T>? page).ShouldBeTrue(result.ToString());
        return page!;
    }

    public static async Task<List<TRow>> All<TRow>(this IReadModelStore<TRow> store, ListQuery query)
        where TRow : class, IProjection<TRow>
    {
        var all = new List<TRow>();
        for (var q = query; ;)
        {
            var page = (await store.Search(q, default)).Ok();
            all.AddRange(page.Items);
            if (page.Next is null)
                return all;
            q = q with { After = page.Next };
        }
    }
}
