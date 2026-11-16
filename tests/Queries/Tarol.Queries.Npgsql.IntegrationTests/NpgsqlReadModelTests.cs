using Npgsql;
using Tarol.Persistence;
using Tarol.Results;
using static Tarol.Queries.Npgsql.IntegrationTests.Setup;

namespace Tarol.Queries.Npgsql.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class NpgsqlReadModelTests(Postgres postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_script_creates_the_table_with_typed_columns_the_key_and_an_index_per_sort()
    {
        var setup = await postgres.Create();
        await setup.ReadModels.Store<AuctionRow>().Prepare(Ct);
        await setup.ReadModels.Store<AuctionRow>().Prepare(Ct);                           // idempotent

        var columns = await Columns(setup.Schema, "rm_auction_list_v1");
        columns.ShouldBe(
        [
            "tenant uuid NO", "id uuid NO", "version bigint NO", "title text NO", "seller text NO", "ends_at timestamp with time zone NO",
            "status integer NO", "highest_bid numeric NO", "bids integer NO", "winner text YES", "doc jsonb NO",
        ]);
        (await postgres.Scalar($"SELECT count(*) FROM pg_indexes WHERE schemaname = '{setup.Schema}' AND tablename = 'rm_auction_list_v1'")).ShouldBe(3L);
        (await postgres.Scalar($"SELECT collation_name FROM information_schema.columns WHERE table_schema = '{setup.Schema}' AND table_name = 'rm_auction_list_v1' AND column_name = 'seller'"))
            .ShouldBe("C");
        (await setup.ReadModels.Store<AuctionRow>().Checkpoint(Ct)).ShouldBe(new ProjectionCheckpoint(GlobalPosition.Start, ProjectionState.Building));
        NpgsqlReadModelSchema.Script<AuctionRow>("x").ShouldContain("CREATE INDEX IF NOT EXISTS rm_auction_list_v1_s3 ON x.rm_auction_list_v1 (tenant, ends_at, id);");
    }

    [Fact]
    public async Task One_row_per_auction_equal_to_the_aggregates_other_stream_types_ignored_tenants_isolated()
    {
        var setup = await postgres.Create();
        var auctions = await setup.Seed(12, TenantA);
        await setup.Seed(3, TenantB);
        var lot = Lot.Register("crate", Postgres.Now);
        (await setup.Store.Append(lot, 0, Context(TenantA), default, Ct)).IsSuccess.ShouldBeTrue();
        var host = setup.Host<AuctionRow>();

        await setup.CatchUp(host);

        var store = setup.ReadModels.Store<AuctionRow>();
        var rows = await store.All(Query(TenantA));
        rows.Count.ShouldBe(12);
        foreach (var auction in auctions)
        {
            var row = (await store.Find(new RowKey(TenantA.Value, auction.Id.Value), Ct)).ShouldNotBeNull();
            row.Version.ShouldBe(auction.Version);
            (row.Row.HighestBid, row.Row.Status, row.Row.Seller).ShouldBe((auction.HighestBid, auction.Status, auction.Seller));
        }
        (await store.All(Query(TenantB))).Count.ShouldBe(3);
        var checkpoint = await store.Checkpoint(Ct);
        checkpoint.State.ShouldBe(ProjectionState.Current);
        checkpoint.Position.ShouldBeGreaterThanOrEqualTo(setup.Last);
    }

    [Fact]
    public async Task Queries_answer_like_the_in_memory_store()
    {
        var setup = await postgres.Create();
        await setup.Seed(30, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);
        var memory = ProjectionTest<AuctionRow>.Over(setup.Store);
        await setup.CatchUp(memory.Host);
        var store = setup.ReadModels.Store<AuctionRow>();

        ListQuery[] queries =
        [
            Query(TenantA) with { Sort = "ends_at", Limit = 7 },
            Query(TenantA) with { Sort = "highest_bid", Descending = true, Limit = 4 },
            Query(TenantA) with { Filters = [new ColumnFilter("status", AuctionStatus.Open)], Sort = "highest_bid", Descending = true, Limit = 3 },
            Query(TenantA) with { Filters = [new ColumnFilter("seller", "ada"), new ColumnFilter("winner", null)], Limit = 2 },
            Query(TenantA) with { Limit = 5 },
            Query(TenantA) with { Descending = true, Limit = 5 },
        ];
        foreach (var query in queries)
        {
            var postgresRows = await store.All(query);
            var memoryRows = await memory.ReadModels.All(query);
            postgresRows.ShouldBe(memoryRows, query.ToString());
            postgresRows.ShouldNotBeEmpty();
        }
        var first = (await store.Search(queries[0], Ct)).Ok();
        var firstInMemory = (await memory.Search(queries[0])).Ok();
        (first.ETag, first.Next, first.Position).ShouldBe((firstInMemory.ETag, firstInMemory.Next, firstInMemory.Position));
        (await store.Search(queries[2] with { Count = true }, Ct)).Ok().Total.ShouldBe(memory.Rows(TenantA).Count(r => r.Status == AuctionStatus.Open));
        (await store.Search(queries[0] with { Offset = 7 }, Ct)).Ok().Items.ShouldBe((await store.Search(queries[0] with { After = first.Next }, Ct)).Ok().Items);
    }

    [Fact]
    public async Task A_cursor_is_bound_to_its_sort_and_filters_and_bounds_are_refused()
    {
        var setup = await postgres.Create();
        await setup.Seed(5, TenantA);
        await setup.CatchUp(setup.Host<AuctionRow>());
        var store = setup.ReadModels.Store<AuctionRow>();
        var next = (await store.Search(Query(TenantA) with { Sort = "ends_at", Limit = 2 }, Ct)).Ok().Next;

        Field(await store.Search(Query(TenantA) with { Sort = "highest_bid", After = next }, Ct)).ShouldBe("after");
        Field(await store.Search(Query(TenantA) with { Sort = "ends_at", Filters = [new ColumnFilter("seller", "bob")], After = next }, Ct)).ShouldBe("after");
        Field(await store.Search(Query(TenantA) with { Offset = 1001 }, Ct)).ShouldBe("offset");
        Field(await store.Search(Query(TenantA) with { Limit = 201 }, Ct)).ShouldBe("limit");
    }

    [Fact]
    public async Task The_page_ETag_is_stable_then_changed_by_a_bid_on_the_page()
    {
        var setup = await postgres.Create();
        var auctions = await setup.Seed(6, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);
        var store = setup.ReadModels.Store<AuctionRow>();
        var query = Query(TenantA) with { Sort = "ends_at", Limit = 3 };
        var etag = (await store.Search(query, Ct)).Ok().ETag;
        (await store.Search(query, Ct)).Ok().ETag.ShouldBe(etag);

        auctions[1].Bid("dora", 500m).IsSuccess.ShouldBeTrue();
        await setup.Commit(auctions[1], TenantA);
        await setup.CatchUp(host);

        (await store.Search(query, Ct)).Ok().ETag.ShouldNotBe(etag);
    }

    [Fact]
    public async Task WaitFor_times_out_without_a_host_and_returns_once_a_host_in_another_process_passed_the_commit()
    {
        var setup = await postgres.Create();
        await setup.Seed(1, TenantA);
        var auction = Auction.Open("ada", "Late", Postgres.Now.AddDays(1), Postgres.Now);
        var position = await setup.Commit(auction, TenantA);
        var api = new ProjectionPositions(new NpgsqlReadModels(postgres.DataSource, Postgres.Json, setup.Schema));   // no host: polls the row
        await setup.ReadModels.Store<AuctionRow>().Prepare(Ct);

        (await api.WaitFor<AuctionRow>(position, TimeSpan.FromMilliseconds(50), Ct)).ShouldBeFalse();
        var waiting = api.WaitFor<AuctionRow>(position, TimeSpan.FromSeconds(30), Ct);
        await setup.CatchUp(setup.Host<AuctionRow>());

        (await waiting).ShouldBeTrue();
        (await api.Checkpoint<AuctionRow>(Ct)).Position.ShouldBeGreaterThanOrEqualTo(position);
    }

    [Fact]
    public async Task Before_any_host_prepared_the_table_the_checkpoint_is_building_and_a_search_is_unavailable()
    {
        var setup = await postgres.Create();
        var store = setup.ReadModels.Store<AuctionRow>();

        (await store.Checkpoint(Ct)).ShouldBe(new ProjectionCheckpoint(GlobalPosition.Start, ProjectionState.Building));
        var result = await store.Search(Query(TenantA), Ct);
        result.TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Error>().ShouldSatisfyAllConditions(
            e => e.Type.ShouldBe(ErrorType.Unavailable),
            e => e.Code.ShouldBe(NpgsqlReadModelStore<AuctionRow>.NotPreparedCode));
    }

    [Fact]
    public async Task A_replay_from_an_older_checkpoint_changes_nothing()
    {
        var setup = await postgres.Create();
        await setup.Seed(10, TenantA);
        await setup.CatchUp(setup.Host<AuctionRow>());
        var before = await Rows(setup);

        await postgres.Execute($"UPDATE {setup.Schema}.projections SET tx = '0', pos = 0, state = 'building'");
        await setup.CatchUp(setup.Host<AuctionRow>());

        (await Rows(setup)).ShouldBe(before);
    }

    [Fact]
    public async Task A_crash_before_the_write_writes_nothing_and_the_restart_applies_every_event_once()
    {
        var setup = await postgres.Create();
        await setup.Seed(9, TenantA);
        var crashing = new ProjectionHost<AuctionRow>(setup.Store, new CrashingStore(setup.ReadModels.Store<AuctionRow>()));

        await Should.ThrowAsync<InvalidOperationException>(() => crashing.CatchUp(Ct));
        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.rm_auction_list_v1")).ShouldBe(0L);

        await setup.CatchUp(setup.Host<AuctionRow>());
        (await postgres.Scalar($"SELECT sum(bids) FROM {setup.Schema}.rm_auction_list_v1")).ShouldBe(Enumerable.Range(0, 9).Sum(i => (long)(i % 4)));
    }

    [Fact]
    public async Task Two_runners_at_once_give_exact_rows_and_the_guard_refuses_batches()
    {
        var setup = await postgres.Create();
        await setup.Seed(60, TenantA);
        var options = new ProjectionOptions { Batch = 7 };
        var one = setup.Host<AuctionRow>(options);
        var two = setup.Host<AuctionRow>(options);

        await Task.WhenAll(Task.Run(() => setup.CatchUp(one), Ct), Task.Run(() => setup.CatchUp(two), Ct));
        await setup.CatchUp(one);

        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.rm_auction_list_v1")).ShouldBe(60L);
        (await postgres.Scalar($"SELECT sum(bids) FROM {setup.Schema}.rm_auction_list_v1")).ShouldBe(Enumerable.Range(0, 60).Sum(i => (long)(i % 4)));
        (await postgres.Scalar($"SELECT sum(version) FROM {setup.Schema}.rm_auction_list_v1"))
            .ShouldBe(await postgres.Scalar($"SELECT sum(version) FROM {setup.Schema}.streams WHERE type = 'auction'"));
    }

    [Fact]
    public async Task The_second_runner_is_refused_deterministically()
    {
        var setup = await postgres.Create();
        await setup.Seed(10, TenantA);
        var one = setup.Host<AuctionRow>(new ProjectionOptions { Batch = 4 });
        var two = setup.Host<AuctionRow>(new ProjectionOptions { Batch = 4 });

        await one.RunOnce(Ct);
        await two.RunOnce(Ct);
        await one.RunOnce(Ct);

        one.Conflicts.ShouldBe(1);
    }

    [Fact]
    public async Task Offset_repeats_a_row_under_an_insert_before_the_page_and_the_cursor_does_not()
    {
        var setup = await postgres.Create();
        await setup.Seed(10, TenantA);
        var host = setup.Host<AuctionRow>();
        await setup.CatchUp(host);
        var store = setup.ReadModels.Store<AuctionRow>();
        var query = Query(TenantA) with { Sort = "ends_at", Limit = 4 };
        var page1 = (await store.Search(query, Ct)).Ok();

        await setup.Commit(Auction.Open("ada", "Early", Postgres.Now.AddMinutes(1), Postgres.Now), TenantA);
        await setup.CatchUp(host);

        (await store.Search(query with { Offset = 4 }, Ct)).Ok().Items[0].ShouldBe(page1.Items[^1]);
        (await store.Search(query with { After = page1.Next }, Ct)).Ok().Items.ShouldNotContain(page1.Items[^1]);
    }

    [Fact]
    public async Task A_new_version_builds_from_the_start_next_to_the_old_one_which_keeps_serving()
    {
        var setup = await postgres.Create();
        await setup.Seed(20, TenantA);
        var v1 = setup.Host<AuctionRow>();
        await setup.CatchUp(v1);
        var v2 = setup.Host<AuctionRowV2>(new ProjectionOptions { Batch = 10 });

        await v2.RunOnce(Ct);
        (await setup.ReadModels.Store<AuctionRowV2>().Checkpoint(Ct)).State.ShouldBe(ProjectionState.Building);
        (await setup.ReadModels.Store<AuctionRow>().All(Query(TenantA))).Count.ShouldBe(20);         // v1 still serves
        await setup.CatchUp(v2);

        (await setup.ReadModels.Store<AuctionRowV2>().Checkpoint(Ct)).State.ShouldBe(ProjectionState.Current);
        var newRows = await setup.ReadModels.Store<AuctionRowV2>().All(Query(TenantA) with { Sort = "ends_at" });
        var oldRows = await setup.ReadModels.Store<AuctionRow>().All(Query(TenantA) with { Sort = "ends_at" });
        newRows.Select(r => (r.Id, r.Bids, r.HighestBid, r.Status)).ShouldBe(oldRows.Select(r => (r.Id, r.Bids, r.HighestBid, r.Status)));
        newRows.Count(r => r.HasBids).ShouldBe(oldRows.Count(r => r.Bids > 0));

        await Should.ThrowAsync<InvalidOperationException>(() => setup.ReadModels.DropVersion("auction-list", 2, Ct));
        await setup.ReadModels.DropVersion("auction-list", 3, Ct);                                 // nothing there: no-op
        var other = new NpgsqlReadModels(postgres.DataSource, Postgres.Json, setup.Schema);         // a process that no longer runs v1
        await other.DropVersion("auction-list", 1, Ct);
        (await postgres.Scalar($"SELECT to_regclass('{setup.Schema}.rm_auction_list_v1')::text")).ShouldBe(DBNull.Value);
        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.projections WHERE name = 'auction-list'")).ShouldBe(1L);
    }

    [Fact]
    public async Task PostgreSQL_refuses_a_timestamptz_column_generated_from_jsonb()
    {
        var setup = await postgres.Create();

        var refused = await Should.ThrowAsync<PostgresException>(() => postgres.Execute(
            $"CREATE TABLE {setup.Schema}.generated (doc jsonb, ends_at timestamptz GENERATED ALWAYS AS ((doc->>'endsAt')::timestamptz) STORED)"));

        refused.SqlState.ShouldBe("42P17");                                                         // so the host writes typed columns itself
    }

    [Fact]
    public async Task An_open_transaction_elsewhere_holds_the_projection_back_until_it_ends()
    {
        var setup = await postgres.Create(notifyFeed: true);
        await setup.Seed(1, TenantA);
        await using var signal = new NpgsqlFeedSignal(postgres.DataSource, setup.Store.FeedChannel);
        var host = setup.Host<AuctionRow>(new ProjectionOptions { Poll = TimeSpan.FromMilliseconds(200) }, signal);
        await host.StartAsync(Ct);
        try
        {
            (await host.WaitFor(setup.Last, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();

            // an older transaction holds the xmin horizon: the feed hides every later commit until it ends
            await using var blocker = await postgres.DataSource.OpenConnectionAsync(Ct);
            await using var open = await blocker.BeginTransactionAsync(Ct);
            await using (var touch = new NpgsqlCommand("SELECT pg_current_xact_id()", blocker, open))
                await touch.ExecuteScalarAsync(Ct);
            var position = await setup.Commit(Auction.Open("bob", "Held", Postgres.Now.AddDays(2), Postgres.Now), TenantA);

            (await host.WaitFor(position, TimeSpan.FromMilliseconds(600), Ct)).ShouldBeFalse();
            await open.RollbackAsync(Ct);
            (await host.WaitFor(position, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();          // the fallback poll picks it up
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_feed_signal_returns_at_a_commit_long_before_its_timeout()
    {
        var setup = await postgres.Create(notifyFeed: true);
        await using var signal = new NpgsqlFeedSignal(postgres.DataSource, setup.Store.FeedChannel);
        await signal.Wait(TimeSpan.FromSeconds(30), Ct);                                       // opens the LISTEN connection (signals once)

        for (var i = 0; i < 3; i++)
        {
            var woken = signal.Wait(TimeSpan.FromMinutes(5), Ct).AsTask();
            await setup.Commit(Auction.Open("ada", $"Woken {i}", Postgres.Now.AddDays(1), Postgres.Now), TenantA);
            (await Task.WhenAny(woken, Task.Delay(TimeSpan.FromSeconds(30), Ct))).ShouldBeSameAs(woken);   // the NOTIFY, not the 5 min
        }
    }

    [Fact]
    public async Task The_stored_state_counts_match_and_a_wrong_path_is_silently_empty()
    {
        var setup = await postgres.Create();
        await setup.Seed(12, TenantA);
        await setup.CatchUp(setup.Host<AuctionRow>());

        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.streams WHERE type = 'auction' AND state->>'status' = 'Open'"))
            .ShouldBe(await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.rm_auction_list_v1 WHERE status = 0"));
        (await postgres.Scalar($"SELECT count(*) FROM {setup.Schema}.streams WHERE type = 'auction' AND state->>'Status' = 'Open'")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_catch_up_of_1e5_events()
    {
        var setup = await postgres.Create();
        // 1,000 auctions of 10 events (one commit each), then copied by SQL to 10,000 streams: 100,000 events
        for (var i = 0; i < 1000; i++)
        {
            var auction = Auction.Open("ada", $"Lot {i}", Postgres.Now.AddMinutes(i), Postgres.Now);
            for (var b = 1; b <= 8; b++)
                auction.Bid("carla", b).IsSuccess.ShouldBeTrue();
            auction.Close().IsSuccess.ShouldBeTrue();
            await setup.Commit(auction, TenantA);
        }
        var s = setup.Schema;
        await postgres.Execute($"""
            INSERT INTO {s}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, parent_id, payload, recorded_at,
                                    actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent)
            SELECT tenant, type, md5(stream_id::text || k)::uuid, version, gen_random_uuid(), alias, alias_version,
                   CASE WHEN entity_id = stream_id THEN md5(stream_id::text || k)::uuid ELSE entity_id END, parent_id, payload, recorded_at,
                   actor_kind, actor_id, on_behalf_of, correlation_id, causation_id, traceparent
            FROM {s}.events, generate_series(1, 9) AS k ORDER BY k, position
            """);
        (await postgres.Scalar($"SELECT count(*) FROM {s}.events")).ShouldBe(100_000L);
        var host = setup.Host<AuctionRow>();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        long read = 0;
        while (read < 100_000L && watch.Elapsed < TimeSpan.FromSeconds(120))
            read += await host.CatchUp(Ct);                                     // an open transaction elsewhere may hold the tail back
        read.ShouldBe(100_000L);
        watch.Stop();

        (await postgres.Scalar($"SELECT count(*) FROM {s}.rm_auction_list_v1")).ShouldBe(10_000L);
        (await postgres.Scalar($"SELECT sum(bids) FROM {s}.rm_auction_list_v1")).ShouldBe(80_000L);
        (await postgres.Scalar($"SELECT count(*) FROM {s}.rm_auction_list_v1 WHERE status = 1")).ShouldBe(10_000L);
        TestContext.Current.SendDiagnosticMessage($"catch-up of 1e5 events: {watch.Elapsed.TotalSeconds:F2} s ({100_000 / watch.Elapsed.TotalSeconds:F0} events/s)");
    }

    private static string Field(Result<Page<AuctionRow>> result)
    {
        result.TryGetValue(out Violation? violation).ShouldBeTrue();
        return violation.ShouldBeOfType<Invalid>().Errors.Single().Field;
    }

    private async Task<string[]> Columns(string schema, string table)
    {
        var columns = new List<string>();
        await using var command = postgres.DataSource.CreateCommand(
            $"SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{table}' ORDER BY ordinal_position");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            columns.Add($"{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}");
        return [.. columns];
    }

    private async Task<string[]> Rows(Setup setup)
    {
        var rows = new List<string>();
        await using var command = postgres.DataSource.CreateCommand($"SELECT tenant, id, version, doc::text FROM {setup.Schema}.rm_auction_list_v1 ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add($"{reader.GetGuid(0)} {reader.GetGuid(1)} {reader.GetInt64(2)} {reader.GetString(3)}");
        return [.. rows];
    }

    /// <summary>Computes the batch, then "crashes" before the write reaches the database.</summary>
    private sealed class CrashingStore(IReadModelStore<AuctionRow> inner) : IReadModelStore<AuctionRow>
    {
        public ValueTask Prepare(CancellationToken cancellationToken) => inner.Prepare(cancellationToken);

        public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => inner.Checkpoint(cancellationToken);

        public ValueTask<IReadOnlyDictionary<RowKey, Versioned<AuctionRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
            => inner.Load(keys, cancellationToken);

        public ValueTask<bool> Write(ProjectionBatch<AuctionRow> batch, CancellationToken cancellationToken)
            => batch.Rows.Count > 0 ? throw new InvalidOperationException("crash before the write") : inner.Write(batch, cancellationToken);

        public ValueTask<Versioned<AuctionRow>?> Find(RowKey key, CancellationToken cancellationToken) => inner.Find(key, cancellationToken);

        public ValueTask<Result<Page<AuctionRow>>> Search(ListQuery query, CancellationToken cancellationToken) => inner.Search(query, cancellationToken);
    }
}
