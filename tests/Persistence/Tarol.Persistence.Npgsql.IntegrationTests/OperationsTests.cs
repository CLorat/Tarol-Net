using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Contract;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>
/// What step 15B adds to the PostgreSQL store (docs/migration/operations.md §11): audited re-drive and discard, the snapshot
/// signals, the root-command filter of the data source it builds, and the data source registered for the read models.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OperationsTests(Postgres postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Notify = ["Notify"];

    private async Task<(NpgsqlAggregateStore Store, OperationContext Context)> WithDeadReaction(int count = 1)
    {
        var store = await postgres.IsolatedStore(_time, OutboxReactions.From(e => e is Member.Joined ? Notify : ReadOnlySpan<string>.Empty));
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(_time), _time);
        for (var i = 0; i < count; i++)
            (await store.Append(Member.Join(Id<Member>.New(_time), "a@x"), 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        foreach (var row in await store.Outbox.Claim(100, TimeSpan.FromMinutes(1), Ct))
            (await store.Outbox.Bury(row, "gave up", Ct)).ShouldBeTrue();
        return (store, context);
    }

    private async Task<List<(string Actor, string Action, string Target, string Detail)>> Audit(string schema)
    {
        var rows = new List<(string, string, string, string)>();
        await using var command = postgres.DataSource.CreateCommand($"SELECT actor, action, target, detail::text FROM {schema}.operations_audit ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    [Fact]
    public async Task Redrive_and_discard_by_a_person_write_the_audit_row_in_the_same_statement()
    {
        var (store, context) = await WithDeadReaction(count: 2);
        IDeadLetterSource source = store.Outbox;
        var page = await source.List(new DeadLetterQuery { Limit = 1 }, Ct);
        page.Items.ShouldHaveSingleItem().Kind.ShouldBe(DeadLetterKinds.Reactions);
        page.Next.ShouldBe(page.Items[0].Id);
        var all = (await source.List(new DeadLetterQuery(), Ct)).Items;
        (all.Count, (await source.List(new DeadLetterQuery(), Ct)).Next).ShouldBe((2, null));
        all[0].Subject.ShouldStartWith("member/");

        (await source.Redrive(all[0].Id, "ops-alice", Ct)).ShouldBeTrue();
        (await source.Discard(all[1].Id, "ops-bob", Ct)).ShouldBeTrue();
        (await source.Discard(all[1].Id, "ops-bob", Ct)).ShouldBeFalse();                                     // nothing, so no audit row

        var audit = await Audit(store.Schema);
        audit.Select(a => (a.Actor, a.Action, a.Target)).ShouldBe([("ops-alice", "redrive", $"reactions:{all[0].Id}"), ("ops-bob", "discard", $"reactions:{all[1].Id}")]);
        using var detail = JsonDocument.Parse(audit[1].Detail);
        detail.RootElement.GetProperty("reaction").GetString().ShouldBe("Notify");
        detail.RootElement.GetProperty("tenant").GetString().ShouldBe(context.Tenant.GetValueOrThrow().Value.ToString());
        (await store.Outbox.Pending(Ct)).ShouldBe(1);                                                        // the re-driven row is back
        (await store.Outbox.DeadLetters(new DeadLetterQuery(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dead_jobs_are_redriven_and_discarded_with_an_audit_row()
    {
        var store = await postgres.IsolatedStore(_time);
        _ = await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "0 2 * * * UTC", true, Start), new JobSchedule("Hourly", Option.None, "0 * * * * UTC", true, Start)], Ct);
        for (var i = 0; i < 2; i++)
            (await store.Jobs.Bury((await store.Jobs.Claim(TimeSpan.FromMinutes(1), Ct)).GetValueOrThrow(), Start.AddDays(1), "gave up", Ct)).ShouldBeTrue();
        IDeadLetterSource source = store.Jobs;
        var dead = (await source.List(new DeadLetterQuery(), Ct)).Items;
        dead.Select(d => d.Kind).ShouldAllBe(k => k == DeadLetterKinds.Jobs);
        dead[0].Subject.ShouldBe("2026-11-17T09:00:00.0000000Z");

        (await source.Redrive(dead[0].Id, "ops-alice", Ct)).ShouldBeTrue();
        (await source.Redrive(dead[0].Id, "ops-alice", Ct)).ShouldBeFalse();                                 // pending already
        (await source.Discard(dead[1].Id, "ops-alice", Ct)).ShouldBeTrue();

        (await Audit(store.Schema)).Select(a => (a.Action, a.Target)).ShouldBe([("redrive", $"jobs:{dead[0].Id}"), ("discard", $"jobs:{dead[1].Id}")]);
    }

    [Fact]
    public async Task The_snapshot_signals_read_the_backlog_age_and_the_dead_counts()
    {
        var (store, _) = await WithDeadReaction(count: 3);
        var context = OperationContext.Start(Actor.User("bob"), Id<ITenant>.New(_time), _time);
        (await store.Append(Member.Join(Id<Member>.New(_time), "b@x"), 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();   // due now
        _ = await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "0 2 * * * UTC", true, Start)], Ct);
        _time.Advance(TimeSpan.FromMinutes(2));
        await postgres.Execute($"ANALYZE {store.Schema}.outbox");
        var provider = new ServiceCollection().AddSingleton(store).AddSingleton<TimeProvider>(_time).BuildServiceProvider();

        var values = new Dictionary<string, double>();
        foreach (var signal in NpgsqlStoreSignals.All)
        {
            signal.Meter.ShouldBe(OperationsSignals.Meter);
            values[signal.Name] = (await signal.Read(provider, Ct)).ShouldHaveSingleItem().Value;
        }

        values[OperationsSignals.ReactionsOldestDue].ShouldBe(120, tolerance: 1);
        values[OperationsSignals.ReactionsDead].ShouldBe(3);
        values[OperationsSignals.ReactionsBacklog].ShouldBe(1);
        values[OperationsSignals.JobsOverdue].ShouldBe(120);
        values[OperationsSignals.JobsDead].ShouldBe(0);
    }

    [Fact]
    public async Task The_data_source_Tarol_builds_traces_commands_only_inside_an_activity()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var test = new ActivitySource("Tarol.Persistence.Npgsql.IntegrationTests");
        using var testListener = new ActivityListener
        {
            ShouldListenTo = s => s == test,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(testListener);

        async Task<(int Roots, int Children)> Run(bool traceRoots)
        {
            spans.Clear();
            var services = new ServiceCollection();
            _ = new TarolBuilder(services, new JsonSerializerOptions(ContractJson.Default.Options), Postgres.Codec())
                .UseNpgsqlStore(postgres.ConnectionString, new NpgsqlStoreOptions { TraceRootCommands = traceRoots }, ensureSchema: false);
            await using var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<NpgsqlDataSource>();
            var marker = "tarol-root-filter-" + Guid.NewGuid().ToString("N");                               // other tests' spans share the listener
            await using (var idle = db.CreateCommand($"SELECT '{marker}'"))                                   // an idle poll
                _ = await idle.ExecuteScalarAsync(Ct);
            using (test.StartActivity("work"))
            await using (var work = db.CreateCommand($"SELECT '{marker}', 2"))                                // inside a request
                _ = await work.ExecuteScalarAsync(Ct);
            var mine = spans.Where(s => s.TagObjects.Any(t => t.Value is string v && v.Contains(marker, StringComparison.Ordinal))).ToList();
            return (mine.Count(s => s.Parent is null), mine.Count(s => s.Parent is not null));
        }

        (await Run(traceRoots: false)).ShouldBe((0, 1));
        (await Run(traceRoots: true)).ShouldBe((1, 1));
    }

    [Fact]
    public async Task A_data_source_the_application_provides_is_registered_for_the_read_models_unless_one_is()
    {
        await using var own = new NpgsqlSlimDataSourceBuilder(postgres.ConnectionString).EnableArrays().Build();
        var services = new ServiceCollection();
        _ = new TarolBuilder(services, new JsonSerializerOptions(ContractJson.Default.Options), Postgres.Codec()).UseNpgsqlStore(_ => own, ensureSchema: false);
        await using (var provider = services.BuildServiceProvider())
            provider.GetRequiredService<NpgsqlDataSource>().ShouldBeSameAs(own);

        await using var aspire = new NpgsqlSlimDataSourceBuilder(postgres.ConnectionString).EnableArrays().Build();
        var withAspire = new ServiceCollection().AddSingleton(aspire);
        _ = new TarolBuilder(withAspire, new JsonSerializerOptions(ContractJson.Default.Options), Postgres.Codec()).UseNpgsqlStore(_ => own, ensureSchema: false);
        await using (var provider = withAspire.BuildServiceProvider())
            provider.GetRequiredService<NpgsqlDataSource>().ShouldBeSameAs(aspire);
    }
}
