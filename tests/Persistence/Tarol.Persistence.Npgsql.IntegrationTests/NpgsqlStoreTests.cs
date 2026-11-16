using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Contract;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>What only a database store has: schema, stored rows, the feed, rebuilds, purges, contention.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlStoreTests(Postgres postgres)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OperationContext Context(Option<Id<ITenant>> tenant, string user = "alice") => OperationContext.Start(Actor.User(user), tenant, _time);

    private OperationContext NewTenant() => Context(Id<ITenant>.New(_time));

    private static Guid TenantOf(OperationContext context) => context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty;

    private static async Task<List<PositionedEvent>> ReadAll(NpgsqlAggregateStore feed, GlobalPosition after = default, int limit = 1000)
    {
        var events = new List<PositionedEvent>();
        await foreach (var e in feed.ReadAll(after, limit, Ct))
            events.Add(e);
        return events;
    }

    // The feed returns a commit once every older transaction of the cluster has ended (P-14), and other tests hold
    // transactions open: wait until at least `count` events are final.
    private static async Task<List<PositionedEvent>> Eventually(NpgsqlAggregateStore feed, int count, GlobalPosition after = default)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var events = await ReadAll(feed, after);
            if (events.Count >= count || waited.Elapsed > TimeSpan.FromSeconds(30))
                return events;
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<T> Committed<T>(NpgsqlAggregateStore store, T aggregate, OperationContext context, long expected = 0)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        (await store.Append(aggregate, expected, context, Option.None, Ct)).ToResult().ShouldBe(Result.Success());
        AggregateChanges.Committed(aggregate);
        return aggregate;
    }

    [Fact]
    public async Task The_state_is_stored_as_queryable_json_and_the_events_with_their_columns()
    {
        var store = postgres.Store(_time);
        var context = NewTenant();
        var member = Member.Join(Id<Member>.New(_time), "a@x");
        member.Earn(5).IsSuccess.ShouldBeTrue();
        member.Award("gold");
        await Committed(store, member, context);

        var where = $"tenant = '{TenantOf(context)}' AND type = 'member' AND id = '{member.Id.Value}'";
        (await postgres.Scalar($"SELECT state->>'email' FROM tarol.streams WHERE {where}")).ShouldBe("a@x");
        (await postgres.Scalar($"SELECT jsonb_array_length(state->'badges') FROM tarol.streams WHERE {where}")).ShouldBe(1);
        (await postgres.Scalar($"SELECT schema FROM tarol.streams WHERE {where}")).ShouldBe(Member.StateSchema);
        (await postgres.Scalar($"SELECT string_agg(alias || '@' || alias_version, ',' ORDER BY version) FROM tarol.events WHERE stream_id = '{member.Id.Value}'"))
            .ShouldBe("member.joined@1,member.earned@1,member.badge.awarded@1");
        (await postgres.Scalar($"SELECT payload->>'email' FROM tarol.events WHERE stream_id = '{member.Id.Value}' AND version = 1")).ShouldBe("a@x");
        (await postgres.Scalar($"SELECT count(DISTINCT recorded_at) FROM tarol.events WHERE stream_id = '{member.Id.Value}'")).ShouldBe(1L);
        (await postgres.Scalar($"SELECT count(*) FROM tarol.events WHERE stream_id = '{member.Id.Value}' AND tenant = '{TenantOf(context)}' AND actor_id = 'alice'")).ShouldBe(3L);
    }

    [Fact]
    public async Task Lookups_and_keys_carry_the_tenant()
    {
        var store = postgres.Store(_time, new StoreLookups().Define<Member>("email", m => m.Email));
        var context = NewTenant();
        var member = Member.Join(Id<Member>.New(_time), "t@x");
        (await store.Append(member, 0, context, new IdempotencyKey(context.Tenant, "test", "k1"), Ct)).IsSuccess.ShouldBeTrue();

        (await postgres.Scalar($"SELECT tenant FROM tarol.lookups WHERE stream_id = '{member.Id.Value}'")).ShouldBe(TenantOf(context));
        (await postgres.Scalar($"SELECT tenant FROM tarol.idempotency_keys WHERE stream_id = '{member.Id.Value}'")).ShouldBe(TenantOf(context));
    }

    [Fact]
    public async Task A_load_by_replay_aggregate_stores_no_state()
    {
        var store = postgres.Store(_time);
        var tally = Tally.Start(Id<Tally>.New(_time));
        tally.Bump();
        await Committed(store, tally, NewTenant());
        (await postgres.Scalar($"SELECT schema FROM tarol.streams WHERE id = '{tally.Id.Value}'")).ShouldBe(0);
        (await postgres.Scalar($"SELECT state FROM tarol.streams WHERE id = '{tally.Id.Value}'")).ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task Another_schema_replays_and_the_next_commit_rewrites_the_state()
    {
        var store = postgres.Store(_time);
        var context = NewTenant();
        var member = await Committed(store, Member.Join(Id<Member>.New(_time), "a@x"), context);
        await postgres.Execute($"UPDATE tarol.streams SET schema = 42, state = '{{\"wrong\":true}}' WHERE id = '{member.Id.Value}'");

        var loaded = (await store.Load(member.Id, context, Ct)).GetValueOrThrow();
        loaded.Email.ShouldBe("a@x");
        loaded.Earn(1).IsSuccess.ShouldBeTrue();
        await Committed(store, loaded, context, expected: 1);

        (await postgres.Scalar($"SELECT schema FROM tarol.streams WHERE id = '{member.Id.Value}'")).ShouldBe(Member.StateSchema);
        (await postgres.Scalar($"SELECT state->>'points' FROM tarol.streams WHERE id = '{member.Id.Value}'")).ShouldBe("1");
    }

    [Fact]
    public async Task A_state_that_does_not_parse_falls_back_to_a_replay()
    {
        var store = postgres.Store(_time);
        var context = NewTenant();
        var member = Member.Join(Id<Member>.New(_time), "a@x");
        member.Earn(3).IsSuccess.ShouldBeTrue();
        await Committed(store, member, context);
        await postgres.Execute($"UPDATE tarol.streams SET state = '{{\"id\":42}}' WHERE id = '{member.Id.Value}'");

        (await store.Load(member.Id, context, Ct)).GetValueOrThrow().Points.ShouldBe(3);
    }

    [Fact]
    public async Task RebuildStates_rewrites_every_stale_row_and_skips_none()
    {
        var store = await postgres.IsolatedStore(_time);
        var context = NewTenant();
        var ids = new List<Id<Member>>();
        for (var i = 0; i < 3; i++)
            ids.Add((await Committed(store, Member.Join(Id<Member>.New(_time), $"m{i}@x"), context)).Id);
        await postgres.Execute($"UPDATE {store.Schema}.streams SET schema = 7, state = NULL WHERE type = 'member'");

        (await store.RebuildStates<Member>(Ct)).ShouldBe(3);
        (await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.streams WHERE schema = {Member.StateSchema} AND state->>'email' LIKE 'm%'")).ShouldBe(3L);
        (await store.RebuildStates<Member>(Ct)).ShouldBe(0);
        (await store.RebuildStates<Tally>(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_v1_payload_is_upcast_on_load_and_an_unknown_alias_throws()
    {
        var codec = Postgres.Codec().Upcast<Member>(new LeftV1ToV2());
        var store = postgres.Store(_time, codec: codec);
        var context = NewTenant();
        var member = await Committed(store, Member.Join(Id<Member>.New(_time), "a@x"), context);
        member.Leave("moved");
        await Committed(store, member, context, expected: 1);
        await postgres.Execute($"UPDATE tarol.events SET alias_version = 1, payload = '{{}}' WHERE stream_id = '{member.Id.Value}' AND version = 2");
        await postgres.Execute($"UPDATE tarol.streams SET schema = 0 WHERE id = '{member.Id.Value}'");   // force the replay

        (await store.Load(member.Id, context, Ct)).GetValueOrThrow().LeftFor.ShouldBe("unknown");

        await postgres.Execute($"UPDATE tarol.events SET alias = 'member.quit' WHERE stream_id = '{member.Id.Value}' AND version = 2");
        (await Should.ThrowAsync<InvalidOperationException>(async () => _ = await store.Load(member.Id, context, Ct)))
            .Message.ShouldContain("'member.quit' v1");
    }

    [Fact]
    public async Task The_feed_fills_RecordedEvent_with_every_column()
    {
        var store = await postgres.IsolatedStore(_time);
        using var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name == "test",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test");
        var cause = Id<ICausation>.New(_time);
        var context = Context(Id<ITenant>.New(_time)).AsSystem("Jobs.Test").CausedBy(cause);
        var member = Member.Join(Id<Member>.New(_time), "a@x");
        var badge = member.Award("gold");
        string? traceparent;
        using (var activity = source.StartActivity("commit"))
        {
            traceparent = activity!.Id;
            (await store.Append(member, 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        }
        await Committed(store, Tally.Start(Id<Tally>.New(_time)), Context(Option.None, "bob"));

        var events = await Eventually(store, 3);

        events.Count.ShouldBe(3);
        events.Select(e => e.Position).ShouldBeInOrder();
        events[0].StreamType.ShouldBe("member");
        events[0].Event.ShouldBe(new RecordedEvent(member.Id.Value, new StreamEvent(1, member.Id.Value, Guid.Empty, new Member.Joined("a@x")),
            events[0].Event.EventId, _time.GetUtcNow(), context));
        events[1].Event.Event.EntityId.ShouldBe(badge.Id.Value);
        events[1].Event.Event.ParentId.ShouldBe(member.Id.Value);
        events[0].Event.EventId.Version.ShouldBe(7);
        events[2].Event.Context.Tenant.IsNone.ShouldBeTrue();
        events[2].Event.Context.Actor.ShouldBe(Actor.User("bob"));
        (await postgres.Scalar($"SELECT DISTINCT traceparent FROM {store.Schema}.events WHERE stream_id = '{member.Id.Value}'")).ShouldBe(traceparent);
        (await ReadAll(store, events[1].Position)).Single().ShouldBe(events[2]);
    }

    [Fact]
    public async Task The_feed_does_not_skip_a_transaction_that_commits_late()
    {
        var store = await postgres.IsolatedStore(_time);
        string Row(Guid stream) => $$"""
            INSERT INTO {{store.Schema}}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, payload, recorded_at,
                                               actor_kind, actor_id, correlation_id)
            VALUES ('{{Guid.Empty}}', 'tally', '{{stream}}', 1, '{{Guid.CreateVersion7()}}', 'tally.started', 1, '{{stream}}', '{}', now(), 'User', 'u', '{{Guid.CreateVersion7()}}')
            """;
        async Task Sql(NpgsqlConnection c, string sql)
        { await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(Ct); }
        await using var older = await postgres.DataSource.OpenConnectionAsync(Ct);
        await using var newer = await postgres.DataSource.OpenConnectionAsync(Ct);

        // the older transaction takes a position first and is still open while the newer one commits a later position
        await Sql(older, "BEGIN");
        await Sql(older, Row(Guid.NewGuid()));
        await Sql(newer, "BEGIN");
        await Sql(newer, Row(Guid.NewGuid()));
        await Sql(newer, "COMMIT");
        (await ReadAll(store)).ShouldBeEmpty();   // not final while the older one runs
        await Sql(older, "COMMIT");
        (await Eventually(store, 2)).Count.ShouldBe(2);

        // an older transaction id with a later position: returned first, then the late commit is still found
        var first = await Eventually(store, 2);
        await Sql(newer, "BEGIN");
        await Sql(newer, "SELECT pg_current_xact_id()");
        await Sql(older, "BEGIN");
        await Sql(older, "SELECT pg_current_xact_id()");
        await Sql(older, Row(Guid.NewGuid()));
        await Sql(newer, Row(Guid.NewGuid()));
        await Sql(newer, "COMMIT");
        var page = await Eventually(store, 1, first[^1].Position);
        page.Count.ShouldBe(1);
        await Sql(older, "COMMIT");
        var late = await Eventually(store, 1, page[0].Position);
        late.Count.ShouldBe(1);
        late[0].Position.Position.ShouldBeLessThan(page[0].Position.Position);   // a position-only checkpoint would have lost it
    }

    // Found while building 8B: ordering by the text output column skipped every transaction id with fewer digits.
    [Fact]
    public async Task Paging_the_feed_returns_every_event_once_while_others_commit()
    {
        var store = await postgres.IsolatedStore(_time);
        using var writing = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!writing.IsCancellationRequested)
                await Committed(store, Member.Join(Id<Member>.New(_time), ""), NewTenant());
        }, Ct)).ToArray();
        await Task.Delay(300, Ct);

        var seen = new List<PositionedEvent>();
        var after = GlobalPosition.Start;
        for (var pages = 0; pages < 200; pages++)
        {
            var page = await ReadAll(store, after, limit: 7);
            if (page.Count > 0)
                after = page[^1].Position;
            seen.AddRange(page);
        }
        await writing.CancelAsync();
        await Task.WhenAll(writers);
        var total = Convert.ToInt32(await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.events"), System.Globalization.CultureInfo.InvariantCulture);
        var waited = Stopwatch.StartNew();
        while (seen.Count < total && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            var rest = await ReadAll(store, after, limit: 7);
            if (rest.Count == 0)
            {
                await Task.Delay(20, Ct);   // the tail is final once older transactions elsewhere end
                continue;
            }
            seen.AddRange(rest);
            after = rest[^1].Position;
        }

        var dups = seen.GroupBy(static e => e.Event.EventId).Where(static g => g.Count() > 1).Select(static g => g.First().Position).ToArray();
        dups.ShouldBeEmpty();
        seen.Select(static e => e.Position).ShouldBeInOrder();
        var all = new List<string>();
        await using (var cmd = postgres.DataSource.CreateCommand($"SELECT tx_id::text || ':' || position FROM {store.Schema}.events ORDER BY tx_id, position"))
        await using (var r = await cmd.ExecuteReaderAsync(Ct))
            while (await r.ReadAsync(Ct))
                all.Add(r.GetString(0));
        var got = seen.Select(static e => $"{e.Position.Transaction}:{e.Position.Position}").ToHashSet();
        var missing = all.Where(x => !got.Contains(x)).ToList();
        missing.ShouldBeEmpty($"missing {missing.Count}: {string.Join(" ", missing.Take(20))}");
    }

    [Fact]
    public async Task Sixty_four_concurrent_writers_leave_a_gapless_stream()
    {
        var store = postgres.Store(_time);
        var context = NewTenant();
        var member = await Committed(store, Member.Join(Id<Member>.New(_time), "a@x"), context);
        int ok = 0, conflicts = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                var loaded = (await store.Load(member.Id, context, Ct)).GetValueOrThrow();
                var expected = loaded.Version;
                loaded.Earn(1).IsSuccess.ShouldBeTrue();
                var result = await store.Append(loaded, expected, context, Option.None, Ct);
                if (result.IsSuccess)
                    Interlocked.Increment(ref ok);
                else if (StoreErrors.IsVersionConflict(result))
                    Interlocked.Increment(ref conflicts);
                else
                    throw new InvalidOperationException(result.ToString());
            }
        }, Ct)));

        (ok + conflicts).ShouldBe(320);
        var final = (await store.Load(member.Id, context, Ct)).GetValueOrThrow();
        final.Version.ShouldBe(1 + ok);
        final.Points.ShouldBe(ok);
        (await postgres.Scalar($"SELECT count(*) = max(version) AND min(version) = 1 FROM tarol.events WHERE stream_id = '{member.Id.Value}'")).ShouldBe(true);
    }

    [Fact]
    public async Task Concurrent_creations_with_one_lookup_key_commit_one()
    {
        var store = postgres.Store(_time, new StoreLookups().Define<Member>("email", m => m.Email));
        var context = NewTenant();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(async () => await store.Append(Member.Join(Id<Member>.New(_time), "same@x"), 0, context, Option.None, Ct), Ct)));
        results.Count(static r => r.IsSuccess).ShouldBe(1);
        results.Where(static r => !r.IsSuccess).ShouldAllBe(r => r.ToResult() == Result.Failure(StoreErrors.LookupTaken("email", "same@x")));
    }

    [Fact]
    public async Task PurgeKeys_deletes_keys_older_than_the_window_in_batches()
    {
        var store = await postgres.IsolatedStore(_time);
        var context = NewTenant();
        for (var i = 0; i < 5; i++)
            (await store.Append(Member.Join(Id<Member>.New(_time), $"{i}@x"), 0, context, new IdempotencyKey(context.Tenant, "test", $"old-{i}"), Ct)).IsSuccess.ShouldBeTrue();
        _time.Advance(TimeSpan.FromDays(8));
        var recent = new IdempotencyKey(context.Tenant, "test", "recent");
        (await store.Append(Member.Join(Id<Member>.New(_time), "r@x"), 0, context, recent, Ct)).IsSuccess.ShouldBeTrue();

        var window = _time.GetUtcNow() - TimeSpan.FromDays(7);
        (await store.PurgeKeys(window, 3, Ct)).ShouldBe(3);
        (await store.PurgeKeys(window, 3, Ct)).ShouldBe(2);
        (await store.PurgeKeys(window, 3, Ct)).ShouldBe(0);
        (await store.Seen(recent, Ct)).IsSome.ShouldBeTrue();
        (await store.Seen(new IdempotencyKey(context.Tenant, "test", "old-0"), Ct)).IsNone.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureSchema_adds_the_fingerprint_column_to_a_key_table_from_before_step_10()
    {
        var store = await postgres.IsolatedStore(_time);
        await postgres.Execute($"ALTER TABLE {store.Schema}.idempotency_keys DROP COLUMN fingerprint");   // the table as 8B created it

        await store.EnsureSchema(Ct);
        var context = NewTenant();
        var key = new IdempotencyKey(context.Tenant, "http", "k-1") { Fingerprint = "sha256:abc" };
        (await store.Append(Member.Join(Id<Member>.New(_time), "a@x"), 0, context, key, Ct)).IsSuccess.ShouldBeTrue();

        (await store.Seen(key, Ct)).GetValueOrThrow().Fingerprint.ShouldBe("sha256:abc");
    }

    [Fact]
    public async Task EnsureSchema_is_idempotent_and_refuses_a_newer_schema()
    {
        var store = await postgres.IsolatedStore(_time);
        await store.EnsureSchema(Ct);
        await store.EnsureSchema(Ct);
        (await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.schema_version")).ShouldBe(1L);
        await postgres.Execute($"UPDATE {store.Schema}.schema_version SET version = {NpgsqlSchema.Version + 1}");
        await Should.ThrowAsync<InvalidOperationException>(() => store.EnsureSchema(Ct));
    }

    [Fact]
    public void The_schema_name_must_be_a_plain_identifier()
    {
        Should.Throw<ArgumentException>(() => new NpgsqlStoreOptions { Schema = "tarol; DROP TABLE x" });
        Should.Throw<ArgumentException>(() => NpgsqlSchema.Script("Tarol"));
        NpgsqlSchema.Script("app_events").ShouldContain("CREATE TABLE IF NOT EXISTS app_events.streams");
    }

    [Fact]
    public async Task An_aggregate_missing_from_the_codec_is_a_programming_error()
    {
        var store = postgres.Store(_time, codec: new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Tally>());
        await Should.ThrowAsync<InvalidOperationException>(async () => _ = await store.Load(Id<Member>.New(_time), NewTenant(), Ct));
    }

    private sealed class LeftV1ToV2 : IEventUpcaster
    {
        public EventType From => new("member.left", 1);
        public EventType Into => new("member.left", 2);
        public void Upcast(JsonObject payload) => payload["reason"] = "unknown";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotifyFeed_sends_one_notification_per_commit_on_the_feed_channel(bool notifyFeed)
    {
        var schema = $"feed_{(notifyFeed ? "on" : "off")}_{Environment.ProcessId}";
        var store = new NpgsqlAggregateStore(postgres.DataSource, Postgres.Codec(), _time, new NpgsqlStoreOptions { Schema = schema, NotifyFeed = notifyFeed });
        await store.EnsureSchema(Ct);
        store.FeedChannel.ShouldBe(schema + "_feed");
        store.NotifiesFeed.ShouldBe(notifyFeed);
        await using var listener = await postgres.DataSource.OpenConnectionAsync(Ct);
        var received = 0;
        listener.Notification += (_, e) => { if (e.Channel == store.FeedChannel) Interlocked.Increment(ref received); };
        await using (var listen = new NpgsqlCommand($"LISTEN {store.FeedChannel}", listener))
            await listen.ExecuteNonQueryAsync(Ct);

        var member = Member.Join(Id<Member>.New(_time), "n@x");
        member.Earn(2).IsSuccess.ShouldBeTrue();
        var tenant = NewTenant();
        (await store.Append(member, 0, tenant, Option.None, Ct)).IsSuccess.ShouldBeTrue();              // one commit, two events
        StoreErrors.IsVersionConflict(await store.Append(Member.Join(member.Id, "y@x"), 0, tenant, Option.None, Ct)).ShouldBeTrue(); // refused: rolled back, no notification
        StoreErrors.IsVersionConflict(await store.Append(Member.Join(member.Id, "x@x"), 0, NewTenant(), Option.None, Ct)).ShouldBeFalse(); // another tenant: commits
        await listener.WaitAsync(notifyFeed ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(300), Ct);
        await listener.WaitAsync(notifyFeed ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(300), Ct);

        received.ShouldBe(notifyFeed ? 2 : 0);
    }
}
