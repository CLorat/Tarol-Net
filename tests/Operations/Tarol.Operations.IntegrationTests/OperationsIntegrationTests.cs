using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Contract;
using Tarol.Persistence.Npgsql;
using Tarol.StronglyTypedId;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tarol.Operations.IntegrationTests.Postgres))]

namespace Tarol.Operations.IntegrationTests;

/// <summary>One PostgreSQL 17 container per run; every test uses a schema of its own.</summary>
public sealed class Postgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _schemas;

    public string ConnectionString => _container.GetConnectionString();

    public string NewSchema() => $"ops{Interlocked.Increment(ref _schemas)}_{Environment.ProcessId}";

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>The snapshot SQL, the admin entry and the retention on a populated store (operations.md §11, 15C).</summary>
[Trait("Category", "Integration")]
public sealed class OperationsIntegrationTests(Postgres postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] Notify = ["Notify"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(Start);

    private async Task<ServiceProvider> Tarol(string schema, TimeSpan? retention = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        var json = new JsonSerializerOptions(ContractJson.Default.Options);
        _ = new TarolBuilder(services, json, new EventCodec(json).Add<Member>())
            .UseNpgsqlStore(postgres.ConnectionString, new NpgsqlStoreOptions
            {
                Schema = schema,
                Reactions = OutboxReactions.From(e => e is Member.Joined ? Notify : ReadOnlySpan<string>.Empty),
            })
            .AddOperations(o => o.DeadRetention = retention);
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<NpgsqlAggregateStore>().EnsureSchema(Ct);
        return provider;
    }

    private async Task Populate(NpgsqlAggregateStore store, int dead, int due)
    {
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(_time), _time);
        for (var i = 0; i < dead; i++)
            (await store.Append(Member.Join(Id<Member>.New(_time), $"d{i}@x"), 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();
        foreach (var row in await store.Outbox.Claim(100, TimeSpan.FromMinutes(1), Ct))
            (await store.Outbox.Bury(row, "gave up", Ct)).ShouldBeTrue();
        for (var i = 0; i < due; i++)
            (await store.Append(Member.Join(Id<Member>.New(_time), $"n{i}@x"), 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();
    }

    private async Task<List<string>> Audit(string schema)
    {
        await using var db = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = db.CreateCommand($"SELECT action || ':' || target || ':' || actor FROM {schema}.operations_audit ORDER BY id");
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.GetString(0));
        return rows;
    }

    [Fact]
    public async Task The_snapshot_reads_the_store_and_the_checks_judge_it()
    {
        var schema = postgres.NewSchema();
        await using var provider = await Tarol(schema);
        var store = provider.GetRequiredService<NpgsqlAggregateStore>();
        await Populate(store, dead: 2, due: 1);
        _ = await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "0 2 * * * UTC", true, Start)], Ct);
        _time.Advance(TimeSpan.FromMinutes(6));                                              // the due row and the job are 6 min late

        var snapshot = await provider.GetRequiredService<OperationsHost>().PollOnce(Ct);

        snapshot.Failures.ShouldBe(0, snapshot.LastError);
        snapshot.Max(OperationsSignals.ReactionsOldestDue).ShouldBe(360);
        snapshot.Max(OperationsSignals.ReactionsDead).ShouldBe(2);
        snapshot.Max(OperationsSignals.JobsOverdue).ShouldBe(360);
        snapshot.Max(OperationsSignals.JobsDead).ShouldBe(0);
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        report.Entries["tarol-ready"].Status.ShouldBe(HealthStatus.Healthy);                // the schema is ensured, the poll fresh
        report.Entries["tarol-reactions"].Status.ShouldBe(HealthStatus.Unhealthy);          // > 5 min
        report.Entries["tarol-jobs"].Status.ShouldBe(HealthStatus.Degraded);                // > 1 min, < 10 min
        report.Entries["tarol-dead-letters"].Status.ShouldBe(HealthStatus.Degraded);
        report.Entries["tarol-live"].Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task The_admin_entry_pages_redrives_and_discards_with_an_audit_row_each()
    {
        var schema = postgres.NewSchema();
        await using var provider = await Tarol(schema);
        var store = provider.GetRequiredService<NpgsqlAggregateStore>();
        await Populate(store, dead: 3, due: 0);
        var admin = provider.GetRequiredService<IDeadLetterAdmin>();
        admin.Kinds.ShouldBe([DeadLetterKinds.Jobs, DeadLetterKinds.Reactions]);

        var page = await admin.List(DeadLetterKinds.Reactions, new DeadLetterQuery { Limit = 2 }, Ct);
        (page.Items.Count, page.Next).ShouldBe((2, page.Items[1].Id));
        (await admin.Redrive(DeadLetterKinds.Reactions, page.Items[0].Id, "ops-alice", Ct)).ShouldBeTrue();
        (await admin.Discard(DeadLetterKinds.Reactions, page.Items[1].Id, "ops-bob", Ct)).ShouldBeTrue();
        (await admin.Redrive(DeadLetterKinds.Reactions, new DeadLetterQuery(), 1000, "ops-carol", Ct)).ShouldBe(1);
        await Should.ThrowAsync<KeyNotFoundException>(() => admin.List("sagas", new DeadLetterQuery(), Ct));

        (await Audit(schema)).ShouldBe([
            $"redrive:reactions:{page.Items[0].Id}:ops-alice",
            $"discard:reactions:{page.Items[1].Id}:ops-bob",
            $"redrive:reactions:{page.Items[1].Id + 1}:ops-carol"]);
        (await store.Outbox.Pending(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task With_a_retention_the_system_job_purges_old_dead_letters_with_an_audit_row()
    {
        var schema = postgres.NewSchema();
        await using var provider = await Tarol(schema, retention: TimeSpan.FromDays(30));
        var store = provider.GetRequiredService<NpgsqlAggregateStore>();
        await Populate(store, dead: 3, due: 0);
        var task = provider.GetServices<MaintenanceTask>().Single(t => t.Name == OperationsTarolBuilderExtensions.PurgeDeadJob);
        task.Every.ShouldBe(TimeSpan.FromHours(1));

        (await task.Run(provider, Start.AddDays(29), Ct)).ShouldBe(0);                      // within the retention
        (await task.Run(provider, Start.AddDays(31), Ct)).ShouldBe(3);

        (await store.Outbox.DeadLetters(new DeadLetterQuery(), Ct)).ShouldBeEmpty();
        (await Audit(schema)).ShouldBe([$"purge:reactions:{OperationsTarolBuilderExtensions.PurgeDeadJob}"]);
        await using var keeps = await Tarol(postgres.NewSchema());
        keeps.GetServices<MaintenanceTask>().ShouldBeEmpty();                                // no retention by default (O-5)
    }
}
