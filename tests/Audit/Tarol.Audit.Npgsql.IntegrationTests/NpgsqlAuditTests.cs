using Microsoft.Extensions.Time.Testing;
using Tarol.Audit.Tests;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Audit.Npgsql.IntegrationTests;

/// <summary>The change log in PostgreSQL (TN-64 AU-B), fed from the Npgsql event store with sealing.</summary>
[Trait("Category", "Integration")]
public sealed class NpgsqlAuditTests(Postgres postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly Id<ITenant> TenantA = Id<ITenant>.From(Guid.Parse("0196f000-0000-7000-8000-0000000000aa"));
    private static readonly Id<ITenant> TenantB = Id<ITenant>.From(Guid.Parse("0196f000-0000-7000-8000-0000000000bb"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Setup
    {
        public required string Schema { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required InMemoryKeyStore Keys { get; init; }
        public required EventCodec Codec { get; init; }
        public required NpgsqlAggregateStore Events { get; init; }
        public required NpgsqlAuditStore Store { get; init; }
        public required AuditHost Host { get; init; }
        public required IAuditLog Log { get; init; }

        public OperationContext As(string user, Id<ITenant>? tenant = null) => OperationContext.Start(Actor.User(user), tenant ?? TenantA, Time);

        public async Task Commit(Child child, string user = "anna", Id<ITenant>? tenant = null)
        {
            (await Events.Append(child, child.Version - child.UncommittedChanges.Length, As(user, tenant), Option.None, Ct)).IsSuccess.ShouldBeTrue();
            AggregateChanges.Committed(child);
            Time.Advance(TimeSpan.FromMinutes(1));
        }

        public async Task<List<AuditEntry>> All(AuditQuery? query = null, Id<ITenant>? tenant = null)
        {
            await Host.CatchUp(Ct);
            var rows = new List<AuditEntry>();
            for (var q = query ?? new AuditQuery(); ;)
            {
                var page = (await Log.Query(q, As("reader", tenant), Ct)).GetValueOrThrow();
                rows.AddRange(page.Items);
                if (page.Next is null)
                    return rows;
                q = q with { After = page.Next };
            }
        }
    }

    private async Task<Setup> Create(int version = 1, Setup? over = null)
    {
        var time = over?.Time ?? new FakeTimeProvider(Start);
        var schema = over?.Schema ?? postgres.Schema();
        var keys = over?.Keys ?? InMemoryKeyStore.Create(time);
        var codec = over?.Codec ?? new EventCodec(AuditJson.Default.Options).Add<Child>();
        var events = over?.Events ?? new NpgsqlAggregateStore(postgres.DataSource, codec, time, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        if (over is null)
            await events.EnsureSchema(Ct);
        var store = new NpgsqlAuditStore(postgres.DataSource, schema);
        var host = new AuditHost(new AuditRegistry { Version = version }.Add<Child>(), store, events, codec, keys, time);
        return new Setup { Schema = schema, Time = time, Keys = keys, Codec = codec, Events = events, Store = store, Host = host, Log = AuditLogs.Over(host, keys) };
    }

    [Fact]
    public async Task Rows_hold_before_and_after_sealed_values_open_until_the_subject_is_erased()
    {
        var s = await Create();
        var child = Child.Register(Id<Child>.New(s.Time), "Mia", "Bären");
        await s.Commit(child);
        child.Rename("Mila").ShouldBe(Result.Success());
        child.Move("Bären", Start).ShouldBe(Result.Success());
        await s.Commit(child, "ben");

        var rows = await s.All();
        var stored = await postgres.Scalar($"SELECT string_agg(after, ' ') FROM {s.Schema}.audit_rows_v1 WHERE field = 'firstName'");
        await s.Keys.EraseSubject(TenantA.Value, child.Id.Value, Ct);
        var erased = await s.All(new AuditQuery { Field = "firstName" });

        rows.Select(r => (r.Kind, r.Field, r.Before, r.After, r.Actor.Id)).ShouldBe([
            (AuditKind.Changed, "firstName", "\"Mia\"", "\"Mila\"", "ben"),
            (AuditKind.Created, "group", null, "\"Bären\"", "anna"),
            (AuditKind.Created, "firstName", null, "\"Mia\"", "anna"),
        ], ignoreOrder: true);
        stored.ShouldBeOfType<string>().ShouldContain("tdp1.");
        stored.ShouldBeOfType<string>().ShouldNotContain("\"Mi");
        erased.Select(r => (r.Before, r.After)).ShouldBe([(null, null), (null, null)]);
    }

    [Fact]
    public async Task Entities_removal_paging_filters_and_tenants_answer_like_the_in_memory_store()
    {
        var s = await Create();
        var child = Child.Register(Id<Child>.New(s.Time), "Mia", "Bären");
        var allergy = child.Note("Nüsse");
        await s.Commit(child);
        allergy.Drop();
        child.Leave().ShouldBe(Result.Success());
        await s.Commit(child);
        for (var i = 0; i < 4; i++)
            await s.Commit(Child.Register(Id<Child>.New(s.Time), $"K{i}", "Füchse"), i % 2 == 0 ? "anna" : "ben");
        await s.Commit(Child.Register(Id<Child>.New(s.Time), "Other", "Wölfe"), tenant: TenantB);

        var all = await s.All(new AuditQuery { Limit = 3 });
        var allergyRows = await s.All(new AuditQuery { Target = allergy.Id.Value });
        var ben = await s.All(new AuditQuery { Actor = "ben", Field = "firstName" });
        var removed = await s.All(new AuditQuery { Kind = AuditKind.Removed });
        var b = await s.All(tenant: TenantB);

        all.Count.ShouldBe(3 + 2 + 8);
        all.Select(r => r.Id).Distinct().Count().ShouldBe(all.Count);
        allergyRows.Select(r => (r.Kind, r.Field, r.After)).ShouldBe([(AuditKind.Removed, null, null), (AuditKind.Created, "substance", "\"Nüsse\"")]);
        ben.Select(r => r.After).ShouldBe(["\"K3\"", "\"K1\""]);
        removed.Select(r => r.Target).ShouldBe([allergy.Id.Value, child.Id.Value], ignoreOrder: true);
        b.Count.ShouldBe(2);
        (await postgres.Scalar($"SELECT count(*) FROM {s.Schema}.audit_values_v1 WHERE target = '{child.Id.Value}'")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_new_version_rebuilds_the_same_rows_and_a_second_runner_cannot_write_a_moved_batch()
    {
        var s = await Create();
        var child = Child.Register(Id<Child>.New(s.Time), "Mia", "Bären");
        await s.Commit(child);
        child.Rename("Mila").ShouldBe(Result.Success());
        await s.Commit(child);
        var first = await s.All();

        var rebuilt = await Create(version: 2, over: s);
        var second = await rebuilt.All();
        var moved = await s.Store.Write(1, new AuditBatch([], new Dictionary<AuditValueKey, string?>(), [], GlobalPosition.Start, GlobalPosition.Start, ProjectionState.Current), Ct);

        second.Select(r => (r.Id, r.Field, r.Before, r.After)).ShouldBe(first.Select(r => (r.Id, r.Field, r.Before, r.After)));
        moved.ShouldBeFalse();                                                                 // the checkpoint is past the start
    }

    [Fact]
    public async Task Reads_and_denials_share_the_list_and_retention_and_tenant_erasure_delete_rows()
    {
        var s = await Create();
        await s.Commit(Child.Register(Id<Child>.New(s.Time), "Mia", "Bären"));
        await s.Host.CatchUp(Ct);
        var old = new AuditEntry(Guid.CreateVersion7(), TenantA.Value, Start.AddDays(-40), AuditKind.Read, Actor.User("anna"), "Child", Guid.CreateVersion7(), null, null, null) { View = "ChildHealth" };
        var denied = old with { Id = Guid.CreateVersion7(), At = Start.AddMinutes(5), Kind = AuditKind.Denied, Code = "Auth.Forbidden", Actor = Actor.User("ben") };
        await s.Store.Record(old, Ct);
        await s.Store.Record(denied, Ct);

        var listed = await s.All();
        var purged = await s.Store.Purge(Start.AddDays(-30), 100, Ct);
        var afterPurge = (await s.All()).Count;
        var forgotten = await s.Store.ForgetTenant(TenantA.Value, Ct);

        listed.First().Kind.ShouldBe(AuditKind.Denied);
        listed.Last().Kind.ShouldBe(AuditKind.Read);
        listed.Count.ShouldBe(4);
        (purged, afterPurge, forgotten).ShouldBe((1, 3, 3));
        (await s.All()).ShouldBeEmpty();
    }
}
