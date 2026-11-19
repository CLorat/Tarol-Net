using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Sagas.Tests;
using Tarol.StronglyTypedId;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Sagas.IntegrationTests.Postgres))]

namespace Tarol.Sagas.IntegrationTests;

/// <summary>One PostgreSQL 17 container per run; every test gets a schema of its own (claims cross tenants).</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=300")                 // above the test pools (up to 200) and their extra data sources
        .Build();
    private int _schemas;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public string NewSchema() => $"s{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = new NpgsqlSlimDataSourceBuilder(_container.GetConnectionString() + ";Maximum Pool Size=100").EnableArrays().Build();
    }

    public async Task<NpgsqlAggregateStore> Store(TimeProvider time, bool notify = false)
    {
        var codec = new EventCodec(new JsonSerializerOptions(AppJson.Default.Options)).Add<Auction>().Add<Payout>().Add<Settlement>();
        var store = new NpgsqlAggregateStore(DataSource, codec, time, new NpgsqlStoreOptions
        {
            Schema = $"s{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}",
            Reactions = GeneratedReactions.Set,
            Notify = notify,
        });
        await store.EnsureSchema(default);
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[Trait("Category", "Integration")]
public sealed class SagaIntegrationTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OperationContext Alice => OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);

    private static async Task Eventually(Func<Task<bool>> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!await done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException(what);
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<T> Load<T>(NpgsqlAggregateStore store, Id<T> id, OperationContext context)
        where T : Tarol.Primitives.AggregateRoot<T>, Tarol.Primitives.IAggregateRoot<T>
        => (await store.Load(id, context, Ct)).GetValueOrThrow();

    [Fact]
    public async Task The_settlement_saga_runs_through_the_hosted_worker()
    {
        var store = await postgres.Store(TimeProvider.System);
        var pipeline = new ChangePipeline(store, TimeProvider.System);
        var host = new ReactionHost(store.Outbox, GeneratedReactions.Set, pipeline, TimeProvider.System,
            new ReactionOptions { Workers = 2, PollInterval = TimeSpan.FromMilliseconds(20) });
        await host.StartAsync(Ct);
        var alice = Alice;
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, TimeProvider.System.GetUtcNow()).GetValueOrThrow();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();

        (await pipeline.Run(new SettleAuction(auction.Id), alice, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        await Eventually(async () => (await store.Load((Id<Settlement>)auction.Id.Value, alice, Ct)).IsSome, "the saga starts");
        (await pipeline.Run(new RecordPayment(auction.Id, 120, "pay-1"), alice, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        await Eventually(async () => (await Load(store, (Id<Settlement>)auction.Id.Value, alice)).Status == SettlementStatus.Completed, "the saga completes");
        await host.StopAsync(Ct);

        (await store.Outbox.Pending(Ct)).ShouldBe(1);                                         // only the 3-day deadline waits
        (await store.Outbox.DeadLetters(10, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_Notify_a_reaction_runs_long_before_the_poll_interval()
    {
        var store = await postgres.Store(TimeProvider.System, notify: true);
        await using var outbox = store.Outbox;
        var pipeline = new ChangePipeline(store, TimeProvider.System);
        var worker = new ReactionWorker(outbox, GeneratedReactions.Set, pipeline, TimeProvider.System,
            new ReactionOptions { PollInterval = TimeSpan.FromMinutes(1) });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = worker.Run(stop.Token);
        await Task.Delay(500, Ct);                                                              // idle: the worker waits on LISTEN
        var alice = Alice;
        var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();

        var clock = Stopwatch.StartNew();
        (await pipeline.Run(new NoteAuction(auction.Id, "ok"), alice, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        await Eventually(async () => (await Load(store, auction.Id, alice)).Acknowledged.Count == 1, "the note is acknowledged");
        var took = clock.Elapsed;
        await stop.CancelAsync();
        await loop;

        took.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Four_workers_run_every_reaction_once_and_each_stream_in_order()
    {
        var store = await postgres.Store(TimeProvider.System);
        var pipeline = new ChangePipeline(store, TimeProvider.System);
        var alice = Alice;
        var auctions = new List<Id<Auction>>();
        for (var s = 0; s < 20; s++)
        {
            var auction = Auction.Create("alice", 100, TimeProvider.System.GetUtcNow());
            for (var n = 1; n <= 10; n++)
                auction.Note("n" + n).IsSuccess.ShouldBeTrue();
            (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
            auctions.Add(auction.Id);
        }
        var verdicts = new ConcurrentBag<Verdict>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loops = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var worker = new ReactionWorker(store.Outbox, GeneratedReactions.Set, pipeline, TimeProvider.System, new ReactionOptions { Batch = 8 });
            while (!stop.IsCancellationRequested)
            {
                var round = await worker.RunOnce(stop.Token);
                foreach (var attempt in round)
                    verdicts.Add(attempt.Verdict);
                if (round.Count == 0)
                    await Task.Delay(10, stop.Token);
            }
        }, Ct)).ToArray();

        await Eventually(async () => await store.Outbox.Pending(Ct) == 0, "every row is done");
        await stop.CancelAsync();
        await Task.WhenAll(loops.Select(l => l.ContinueWith(_ => { }, TaskScheduler.Default)));

        verdicts.Count.ShouldBe(200);
        verdicts.ShouldAllBe(v => v == Verdict.Done);
        foreach (var id in auctions)
            (await Load(store, id, alice)).Acknowledged.ShouldBe([.. Enumerable.Range(1, 10).Select(n => "n" + n)]);
    }

    [Fact]
    public async Task A_worker_that_stalls_after_the_commit_is_redelivered_and_the_key_commits_once()
    {
        var time = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var store = await postgres.Store(time);
        var pipeline = new ChangePipeline(store, time);
        var alice = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), time);
        var auction = Auction.Create("alice", 100, time.GetUtcNow());
        _ = auction.PlaceBid("bob", 120, time.GetUtcNow()).GetValueOrThrow();
        (await store.Append(auction, 0, alice, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        (await pipeline.Run(new SettleAuction(auction.Id), alice, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var options = new ReactionOptions { Lease = TimeSpan.FromSeconds(30) };

        var stalled = (await store.Outbox.Claim(10, options.Lease, Ct)).Single(r => r.Reaction == "MarkPayoutDue");
        _ = (await GeneratedReactions.Set.Run(pipeline, stalled.Reaction, stalled.Event.Event, Ct)).GetValueOrThrow();   // committed, never completed
        time.Advance(TimeSpan.FromSeconds(31));
        var redelivered = await new ReactionWorker(store.Outbox, GeneratedReactions.Set, pipeline, time, options).Drain(Ct);

        redelivered.Single(a => a.Row.Reaction == "MarkPayoutDue").ShouldSatisfyAllConditions(a => a.Row.Attempts.ShouldBe(2), a => a.Verdict.ShouldBe(Verdict.Done));
        (await store.Outbox.Complete(stalled, Ct)).ShouldBeFalse();
        await using var count = postgres.DataSource.CreateCommand($"SELECT count(DISTINCT stream_id) FROM {store.Schema}.events WHERE type = 'payout'");
        (await count.ExecuteScalarAsync(Ct)).ShouldBe(1L);
    }
}
