using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

/// <summary>
/// The pushed checkpoint (step 13A, migration/caching.md §11): the batch transaction's <c>NOTIFY</c>, the listener of
/// <see cref="NpgsqlProjectionPositions"/>, its poll fallback and reconnect. Every wait is for a known position with a
/// generous cap; nothing counts inside a wall-clock window.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PushedCheckpointTests(Postgres postgres)
{
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_listener_learns_the_checkpoint_from_the_notify_of_the_batch()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        await using var positions = new NpgsqlProjectionPositions(postgres.DataSource, setup.Schema) { PollInterval = TimeSpan.FromHours(1) };
        (await positions.Checkpoint<AuctionRow>(Ct)).ShouldBe(default);                // starts the listener; no table yet
        await Until(() => positions.IsListening);                                     // set once LISTEN has run

        await setup.Seed(5, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);                                                     // the poll is an hour away: only the NOTIFY moves it

        (await positions.WaitFor<AuctionRow>(host.Position, Cap, Ct)).ShouldBeTrue();
        var pushed = await positions.Checkpoint<AuctionRow>(Ct);
        pushed.ShouldBe(await setup.ReadModels.Store<AuctionRow>().Checkpoint(Ct));
        pushed.Position.ShouldBeGreaterThanOrEqualTo(setup.Last);
        pushed.State.ShouldBe(ProjectionState.Current);
    }

    [Fact]
    public async Task While_the_listen_connection_is_down_the_poll_moves_the_checkpoint()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        var name = $"positions-{Guid.NewGuid():N}";
        await using var db = Own(name);
        var log = new Events();
        await using var positions = new NpgsqlProjectionPositions(db, setup.Schema, logger: log)
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            Backoff = TimeSpan.FromHours(1),                                           // no reconnect in this test: only the poll
        };
        await positions.Checkpoint<AuctionRow>(Ct);
        await Until(() => positions.IsListening);

        await Terminate(name);
        await Until(() => !positions.IsListening && log.Has(12101) && log.Has(12102));

        await setup.Seed(3, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);                                                     // its NOTIFY reaches no listener
        (await positions.WaitFor<AuctionRow>(host.Position, Cap, Ct)).ShouldBeTrue();
        positions.IsListening.ShouldBeFalse();
    }

    [Fact]
    public async Task After_the_listen_connection_is_terminated_it_reconnects_and_listens_again()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        var name = $"positions-{Guid.NewGuid():N}";
        await using var db = Own(name);
        var log = new Events();
        await using var positions = new NpgsqlProjectionPositions(db, setup.Schema, logger: log)
        {
            PollInterval = TimeSpan.FromHours(1),
            Backoff = TimeSpan.FromMilliseconds(50),
        };
        await positions.Checkpoint<AuctionRow>(Ct);
        await Until(() => positions.IsListening);

        await Terminate(name);
        await Until(() => log.Has(12101) && log.Has(12103) && positions.IsListening);

        await setup.Seed(2, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);                                                     // the poll is an hour away: the new LISTEN delivers
        (await positions.WaitFor<AuctionRow>(host.Position, Cap, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task WaitFor_in_another_process_is_woken_by_the_worker_batch()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        var host = setup.Host<AuctionRow>();
        await using var worker = new NpgsqlProjectionPositions(postgres.DataSource, setup.Schema, [host]);
        await using var api = new NpgsqlProjectionPositions(postgres.DataSource, setup.Schema) { PollInterval = TimeSpan.FromHours(1) };
        await api.Checkpoint<AuctionRow>(Ct);
        await Until(() => api.IsListening);

        var position = await setup.Commit(Auction.Open("ada", "Read after", Postgres.Now.AddDays(1), Postgres.Now), TenantA);
        var waiting = api.WaitFor<AuctionRow>(position, Cap, Ct).AsTask();
        var hosted = worker.WaitFor<AuctionRow>(position, Cap, Ct).AsTask();           // the host's own wait in its process
        waiting.IsCompleted.ShouldBeFalse();
        await setup.CatchUp(host);

        (await waiting).ShouldBeTrue();
        (await hosted).ShouldBeTrue();
        (await api.Checkpoint<AuctionRow>(Ct)).Position.ShouldBeGreaterThanOrEqualTo(position);
    }

    [Fact]
    public async Task A_sql_projection_batch_sends_the_notice_too()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        var projection = new HostingTests.SellerTotals(setup.Schema);
        await using var listen = await postgres.DataSource.OpenConnectionAsync(Ct);
        var notices = new ConcurrentQueue<string>();
        listen.Notification += (_, e) => notices.Enqueue(e.Payload);
        await using (var command = new NpgsqlCommand($"LISTEN {setup.Schema}_checkpoint", listen))
            await command.ExecuteNonQueryAsync(Ct);

        await setup.Seed(3, TenantA);
        var host = new SqlProjectionHost(projection, setup.Store, setup.ReadModels);
        await setup.CatchUp(host);
        while (!notices.Any(n => n.StartsWith("seller-totals:1:", StringComparison.Ordinal) && n.EndsWith(":current", StringComparison.Ordinal)))
            (await listen.WaitAsync(30_000, Ct)).ShouldBeTrue("no checkpoint notice of the SQL projection");
    }

    [Fact]
    public async Task Positions_only_move_forward_and_a_malformed_notice_is_ignored()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        await using var positions = new NpgsqlProjectionPositions(postgres.DataSource, setup.Schema) { PollInterval = TimeSpan.FromHours(1) };
        await setup.Seed(2, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);
        (await positions.WaitFor<AuctionRow>(host.Position, Cap, Ct)).ShouldBeTrue();
        var current = await positions.Checkpoint<AuctionRow>(Ct);
        await Until(() => positions.IsListening);                                     // a notice before LISTEN is lost (the poll covers that)

        var channel = setup.Schema + "_checkpoint";
        await postgres.Execute($"SELECT pg_notify('{channel}', 'auction-list:1:1:1:building'), pg_notify('{channel}', 'auction-list:1:x'), pg_notify('{channel}', 'nonsense')");
        await postgres.Execute($"SELECT pg_notify('{channel}', 'auction-list:2:9:9:current')");
        (await positions.WaitFor<AuctionRowV2>(new GlobalPosition(9, 9), Cap, Ct)).ShouldBeTrue();      // sent after the others: they were handled

        (await positions.Checkpoint<AuctionRow>(Ct)).ShouldBe(current);
    }

    [Fact]
    public async Task UseNpgsqlReadModels_with_PushCheckpoints_registers_the_pushed_positions_and_the_sending_stores()
    {
        var services = new ServiceCollection();
        services.AddSingleton(postgres.DataSource);
        new TarolBuilder(services, Postgres.Json, new EventCodec(Postgres.Json).Add<Auction>())
            .UseNpgsqlStore(static provider => provider.GetRequiredService<NpgsqlDataSource>(), new NpgsqlStoreOptions { Schema = $"pushed_{Environment.ProcessId}" })
            .UseNpgsqlReadModels(new NpgsqlReadModelOptions { PushCheckpoints = true });
        await using var provider = services.BuildServiceProvider();

        var positions = provider.GetRequiredService<IProjectionPositions>().ShouldBeOfType<NpgsqlProjectionPositions>();
        positions.Channel.ShouldBe($"pushed_{Environment.ProcessId}_checkpoint");
        var readModels = provider.GetRequiredService<NpgsqlReadModels>();
        readModels.PushesCheckpoints.ShouldBeTrue();
        readModels.Typed<AuctionRow>().PushesCheckpoints.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_PushCheckpoints_nothing_is_sent_and_the_positions_are_polled()
    {
        var services = new ServiceCollection();
        services.AddSingleton(postgres.DataSource);
        new TarolBuilder(services, Postgres.Json, new EventCodec(Postgres.Json).Add<Auction>())
            .UseNpgsqlStore(static provider => provider.GetRequiredService<NpgsqlDataSource>(), new NpgsqlStoreOptions { Schema = $"unpushed_{Environment.ProcessId}" })
            .UseNpgsqlReadModels();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionPositions>().ShouldBeOfType<ProjectionPositions>();
        provider.GetRequiredService<NpgsqlReadModels>().Typed<AuctionRow>().PushesCheckpoints.ShouldBeFalse();
    }

    [Fact]
    public void A_schema_whose_channel_is_too_long_is_refused()
        => Should.Throw<ArgumentException>(() => new NpgsqlProjectionPositions(postgres.DataSource, new string('s', 60)));

    private NpgsqlDataSource Own(string applicationName)
        => new NpgsqlSlimDataSourceBuilder(postgres.ConnectionString + $";Application Name={applicationName}").EnableArrays().Build();

    private async Task Terminate(string applicationName)
        => (await postgres.Scalar($"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = '{applicationName}'"))
            .ShouldBeOfType<long>().ShouldBeGreaterThan(0);

    internal static Task Until(Func<bool> condition) => Until(() => Task.FromResult(condition()));

    internal static async Task Until(Func<Task<bool>> condition)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            waited.Elapsed.ShouldBeLessThan(Cap, "the condition did not hold");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class Events : ILogger<NpgsqlProjectionPositions>
    {
        private readonly ConcurrentQueue<int> _events = new();

        public bool Has(int eventId) => _events.Contains(eventId);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _events.Enqueue(eventId.Id);
    }
}

/// <summary>
/// Tests that hold the xmin horizon with an open transaction. The horizon is cluster-wide, so it hides commits from every
/// other test on the container: they run alone, after the parallel collections.
/// </summary>
[CollectionDefinition(nameof(XminHorizon), DisableParallelization = true)]
public sealed class XminHorizon;

[Collection(nameof(XminHorizon))]
[Trait("Category", "Integration")]
public sealed class PushedCheckpointXminTests(Postgres postgres)
{
    private static readonly TimeSpan Cap = PushedCheckpointTests.Cap;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_checkpoint_does_not_move_past_a_commit_while_an_older_transaction_is_open()
    {
        var setup = await postgres.Create(pushCheckpoints: true);
        await using var positions = new NpgsqlProjectionPositions(postgres.DataSource, setup.Schema);
        await setup.Seed(2, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);
        (await positions.WaitFor<AuctionRow>(host.Position, Cap, Ct)).ShouldBeTrue();

        await using var open = await postgres.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await open.BeginTransactionAsync(Ct);
        await using (var xid = new NpgsqlCommand("SELECT pg_current_xact_id()", open, transaction))
            await xid.ExecuteScalarAsync(Ct);                                          // holds the xmin horizon below the next commit
        var auction = Auction.Open("ada", "Behind an open transaction", Postgres.Now.AddDays(1), Postgres.Now);
        var hidden = await setup.Commit(auction, TenantA);

        await host.CatchUp(Ct);                                                        // the feed hides the commit
        host.Position.ShouldBeLessThan(hidden);
        (await positions.WaitFor<AuctionRow>(hidden, TimeSpan.FromMilliseconds(300), Ct)).ShouldBeFalse();
        (await positions.Checkpoint<AuctionRow>(Ct)).Position.ShouldBeLessThan(hidden);

        await transaction.CommitAsync(Ct);
        await setup.CatchUp(host);
        (await positions.WaitFor<AuctionRow>(hidden, Cap, Ct)).ShouldBeTrue();
        var page = (await setup.ReadModels.Store<AuctionRow>().Search(Query(TenantA), Ct)).Ok();
        page.Items.ShouldContain(row => row.Id == auction.Id.Value);
    }
}
