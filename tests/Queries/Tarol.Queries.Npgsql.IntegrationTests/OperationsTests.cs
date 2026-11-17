using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>
/// Step 15B in Tarol.Queries.Npgsql (docs/migration/operations.md §11; O-5, O-8): one runner per projection version, the
/// take-over after the runner's session ends, and the lag signal.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OperationsTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), what);
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task One_host_runs_a_projection_version_and_another_takes_over_after_its_session_is_terminated()
    {
        var setup = await postgres.Create();
        await setup.Seed(10, TenantA);
        // two processes: the first notices a lost session after 2 s, the second asks every 100 ms, so the second takes over
        // (a host that notices first may take its gate back; either way one host runs)
        var slow = new NpgsqlProjectionGate(postgres.DataSource, setup.Schema) { CheckEvery = TimeSpan.FromSeconds(2) };
        var gate = new NpgsqlProjectionGate(postgres.DataSource, setup.Schema) { RetryEvery = TimeSpan.FromMilliseconds(100) };
        var options = new ProjectionOptions { Poll = TimeSpan.FromMilliseconds(20) };
        using var first = new ProjectionHost<AuctionRow>(setup.Store, setup.ReadModels.Store<AuctionRow>(), options, gate: slow);
        using var second = new ProjectionHost<AuctionRow>(setup.Store, setup.ReadModels.Store<AuctionRow>(), options, gate: gate);
        await first.StartAsync(Ct);
        await Eventually(() => first.Position >= setup.Last, "the first host caught up");
        await second.StartAsync(Ct);
        await setup.Seed(5, TenantA);
        await Eventually(() => first.Position >= setup.Last, "the first host projects the new commits");

        (first.IsRunner, second.IsRunner, second.Position).ShouldBe((true, false, GlobalPosition.Start));   // the second only asks

        // the runner's session ends (a crash, a failover): its lock dies with it
        var key = slow.Key(first.Name, first.Version);
        (await postgres.Scalar($"""
            SELECT count(pg_terminate_backend(l.pid)) FROM pg_locks l
            WHERE l.locktype = 'advisory' AND l.objsubid = 1 AND l.objid::bigint = (hashtext('{key}')::bigint & 4294967295)
            """)).ShouldBe(1L);
        await Eventually(() => second.IsRunner, "the second host took over");
        await setup.Seed(5, TenantA);
        await Eventually(() => second.Position >= setup.Last, "the second host projects");
        await Eventually(() => !first.IsRunner, "the first host noticed it lost the gate");
        await first.StopAsync(Ct);
        await second.StopAsync(Ct);

        (await setup.ReadModels.Store<AuctionRow>().All(Query(TenantA))).Count.ShouldBe(20);
        (await postgres.Scalar($"SELECT state FROM {setup.Schema}.projections WHERE name = 'auction-list'")).ShouldBe("current");
    }

    [Fact]
    public async Task The_lag_signal_is_the_feed_heads_time_minus_the_checkpoints_per_projection()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Postgres.Now);
        var setup = await postgres.Create();
        var store = new NpgsqlAggregateStore(postgres.DataSource, new EventCodec(Postgres.Json).Add<Auction>().Add<Lot>(), time,
            new NpgsqlStoreOptions { Schema = setup.Schema });
        var timed = setup with { Store = store };
        await timed.Seed(3, TenantA);
        var host = timed.Host<AuctionRow>();
        await timed.CatchUp(host);
        var services = new ServiceCollection().AddSingleton(store).AddSingleton(postgres.DataSource);
        _ = new TarolBuilder(services, Postgres.Json, new EventCodec(Postgres.Json)).UseNpgsqlReadModels(new NpgsqlReadModelOptions { Schema = setup.Schema });
        await using var provider = services.BuildServiceProvider();
        var lag = provider.GetServices<OperationsSignal>().Single(s => s.Name == OperationsSignals.ProjectionsLag);

        (await lag.Read(provider, Ct)).ShouldBe([new SignalValue(0, "tarol.projection", "auction-list")]);

        time.Advance(TimeSpan.FromSeconds(45));
        await timed.Seed(1, TenantA);                                                            // 45 s after the checkpoint's event
        (await lag.Read(provider, Ct)).Single().Value.ShouldBe(45, tolerance: 0.001);
        await timed.CatchUp(host);
        (await lag.Read(provider, Ct)).Single().Value.ShouldBe(0);
        lag.Unit.ShouldBe("s");
    }
}

/// <summary>
/// <c>DropVersion</c> behind a reader of the old table (O-5): it gives up after its lock timeouts instead of making every new
/// reader wait behind it. It holds a transaction open, so it runs alone.
/// </summary>
[Collection(nameof(XminHorizon))]
[Trait("Category", "Integration")]
public sealed class DropVersionTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_drop_behind_a_reader_times_out_and_readers_do_not_queue_behind_it()
    {
        var setup = await postgres.Create();
        await postgres.Execute($"""
            CREATE TABLE {setup.Schema}.rm_old_list_v1 (id int);
            INSERT INTO {setup.Schema}.rm_old_list_v1 VALUES (1);
            CREATE TABLE IF NOT EXISTS {setup.Schema}.projections (name text NOT NULL, version integer NOT NULL, tx xid8 NOT NULL DEFAULT '0',
                pos bigint NOT NULL DEFAULT 0, state text NOT NULL DEFAULT 'building', updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (name, version));
            INSERT INTO {setup.Schema}.projections (name, version) VALUES ('old-list', 1);
            """);
        await using var reader = await postgres.DataSource.OpenConnectionAsync(Ct);
        var reading = await reader.BeginTransactionAsync(Ct);
        await using (var read = new NpgsqlCommand($"SELECT count(*) FROM {setup.Schema}.rm_old_list_v1", reader, reading))   // an old pod reads
            _ = await read.ExecuteScalarAsync(Ct);
        var models = new NpgsqlReadModels(postgres.DataSource, Postgres.Json, setup.Schema) { DropLockTimeout = TimeSpan.FromMilliseconds(200), DropAttempts = 3 };

        var drop = Stopwatch.StartNew();
        var dropping = models.DropVersion("old-list", 1, Ct);
        await Task.Delay(50, Ct);
        var another = Stopwatch.StartNew();                                                       // a reader arriving meanwhile
        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.rm_old_list_v1")).ShouldBe(1L);
        another.Stop();
        await Should.ThrowAsync<TimeoutException>(() => dropping);
        drop.Stop();

        another.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));                                // at most one lock timeout, never the reader's 3 s
        drop.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        await reading.CommitAsync(Ct);
        await models.DropVersion("old-list", 1, Ct);                                              // nobody reads it any more
        (await postgres.Scalar($"SELECT to_regclass('{setup.Schema}.rm_old_list_v1')::text")).ShouldBe(DBNull.Value);
    }
}
