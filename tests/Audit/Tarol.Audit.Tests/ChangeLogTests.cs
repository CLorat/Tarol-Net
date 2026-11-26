using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Audit.Tests;

/// <summary>The change log from the feed (TN-64): rows per field with before/after, completeness, entities, removal, erasure, paging.</summary>
public sealed class ChangeLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly Id<ITenant> TenantA = Id<ITenant>.From(Guid.Parse("0196f000-0000-7000-8000-0000000000aa"));
    private static readonly Id<ITenant> TenantB = Id<ITenant>.From(Guid.Parse("0196f000-0000-7000-8000-0000000000bb"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class Fixture
    {
        public FakeTimeProvider Time { get; } = new(Start);
        public InMemoryKeyStore Keys { get; }
        public EventCodec Codec { get; } = new EventCodec(AuditJson.Default.Options).Add<Child>();
        public InMemoryAggregateStore Events { get; }
        public InMemoryAuditStore Store { get; } = new();
        public AuditHost Host { get; }
        public IAuditLog Log { get; }

        public Fixture(bool sealing = true)
        {
            Keys = InMemoryKeyStore.Create(Time);
            Events = new InMemoryAggregateStore(Time, new InMemoryStoreOptions { Codec = Codec, Keys = sealing ? Keys : null });
            var registry = new AuditRegistry().Add<Child>();
            Host = new AuditHost(registry, Store, Events, Codec, sealing ? Keys : null, Time);
            Log = new AuditLog(Host, sealing ? Keys : null);
        }

        public OperationContext As(string user, Id<ITenant>? tenant = null)
            => OperationContext.Start(Actor.User(user), tenant ?? TenantA, Time);

        public async Task Commit(Child child, string user = "anna", Id<ITenant>? tenant = null, Capture? capture = null)
        {
            var context = As(user, tenant);
            if (capture is not null)
                context = context with { Capture = capture };
            (await Events.Append(child, child.Version - child.UncommittedChanges.Length, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();
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

    private static Id<Child> NewChild(FakeTimeProvider time) => Id<Child>.New(time);

    [Fact]
    public async Task Each_changed_field_is_a_row_with_before_and_after_and_unchanged_or_unaudited_fields_are_none()
    {
        var f = new Fixture();
        var child = Child.Register(NewChild(f.Time), "Mia", "Bären");
        await f.Commit(child);
        child.Rename("Mila").ShouldBe(Result.Success());
        child.Move("Bären", Start.AddHours(-1)).ShouldBe(Result.Success());          // same group: no row
        await f.Commit(child, "ben");

        var rows = await f.All();

        rows.Select(r => (r.Kind, r.Field, r.Before, r.After, r.Actor.Id)).ShouldBe([
            (AuditKind.Changed, "firstName", "\"Mia\"", "\"Mila\"", "ben"),
            (AuditKind.Created, "group", null, "\"Bären\"", "anna"),
            (AuditKind.Created, "firstName", null, "\"Mia\"", "anna"),
        ], ignoreOrder: true);
        rows.ShouldNotContain(r => r.Field == "checksum" || r.Field == "at");             // [NotAudited], [AuditTime]
        rows.ShouldAllBe(r => r.StreamType == "child" && r.Target == child.Id.Value && r.Stream == child.Id.Value && r.Tenant == TenantA.Value);
        rows[0].At.ShouldBeGreaterThanOrEqualTo(rows[^1].At);                              // newest first
    }

    [Fact]
    public async Task The_device_time_comes_from_AuditTime_else_from_the_capture()
    {
        var f = new Fixture();
        var child = Child.Register(NewChild(f.Time), "Mia", "Bären");
        await f.Commit(child, capture: new Capture("tab-7", Start.AddMinutes(-30)));
        child.Move("Füchse", Start.AddHours(-2)).ShouldBe(Result.Success());
        await f.Commit(child, capture: new Capture("tab-7", Start.AddMinutes(-5)));

        var rows = await f.All();

        rows.Single(r => r.Field == "group" && r.Kind == AuditKind.Changed).CapturedAt.ShouldBe(Start.AddHours(-2));   // the member wins
        rows.Single(r => r.Field == "firstName").CapturedAt.ShouldBe(Start.AddMinutes(-30));
        rows.ShouldAllBe(r => r.Device == "tab-7");
    }

    [Fact]
    public async Task An_entity_is_its_own_target_and_a_deleted_entity_or_removed_aggregate_is_one_removed_row()
    {
        var f = new Fixture();
        var child = Child.Register(NewChild(f.Time), "Mia", "Bären");
        var allergy = child.Note("Nüsse");
        await f.Commit(child);
        allergy.Drop();
        await f.Commit(child);
        child.Look().ShouldBe(Result.Success());                                           // [NotAudited] event: no row
        child.Leave().ShouldBe(Result.Success());
        await f.Commit(child);

        var rows = await f.All();

        rows.Where(r => r.Target == allergy.Id.Value).Select(r => (r.Kind, r.Field, r.After)).ShouldBe([(AuditKind.Removed, null, null), (AuditKind.Created, "substance", "\"Nüsse\"")]);
        rows.Where(r => r.Target == allergy.Id.Value).ShouldAllBe(r => r.Stream == child.Id.Value);
        rows.First().Kind.ShouldBe(AuditKind.Removed);
        rows.First().Target.ShouldBe(child.Id.Value);
        rows.ShouldNotContain(r => r.Event == "child.looked");
    }

    [Fact]
    public async Task A_personal_value_is_stored_sealed_and_reads_as_null_once_its_subject_is_erased()
    {
        var f = new Fixture();
        var child = Child.Register(NewChild(f.Time), "Mia", "Bären");
        await f.Commit(child);
        child.Rename("Mila").ShouldBe(Result.Success());
        await f.Commit(child);
        await f.Host.CatchUp(Ct);

        var stored = await f.Store.Query(1, TenantA.Value, new AuditQuery { Field = "firstName" }, null, 10, Ct);
        var before = await f.All(new AuditQuery { Field = "firstName" });
        await f.Keys.EraseSubject(TenantA.Value, child.Id.Value, Ct);
        var after = await f.All(new AuditQuery { Field = "firstName" });

        stored.ShouldAllBe(r => r.After != null && r.After.Contains("tdp1.", StringComparison.Ordinal) && !r.After.Contains("\"Mi", StringComparison.Ordinal));
        before.Select(r => r.After).ShouldBe(["\"Mila\"", "\"Mia\""]);
        after.Select(r => (r.Before, r.After)).ShouldBe([(null, null), (null, null)]);         // crypto-shredded, rows kept
        (await f.All(new AuditQuery { Field = "group" })).Single().After.ShouldBe("\"Bären\"");
    }

    [Fact]
    public async Task A_rename_to_the_same_name_is_no_row_although_the_seal_differs()
    {
        var f = new Fixture();
        var child = Child.Register(NewChild(f.Time), "Mia", "Bären");
        await f.Commit(child);
        child.Rename("Mia").ShouldBe(Result.Success());
        await f.Commit(child);

        (await f.All(new AuditQuery { Field = "firstName" })).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Filters_cursor_pages_and_tenants_are_kept_apart()
    {
        var f = new Fixture(sealing: false);
        for (var i = 0; i < 5; i++)
            await f.Commit(Child.Register(NewChild(f.Time), $"K{i}", "Bären"), i % 2 == 0 ? "anna" : "ben");
        await f.Commit(Child.Register(NewChild(f.Time), "Other", "Wölfe"), tenant: TenantB);

        var paged = await f.All(new AuditQuery { Limit = 3 });
        var anna = await f.All(new AuditQuery { Actor = "anna", Field = "firstName" });
        var window = await f.All(new AuditQuery { From = Start.AddMinutes(1), To = Start.AddMinutes(2) });
        var b = await f.All(tenant: TenantB);

        paged.Count.ShouldBe(10);
        paged.Select(r => r.Id).Distinct().Count().ShouldBe(10);
        anna.Select(r => r.After).ShouldBe(["\"K4\"", "\"K2\"", "\"K0\""]);
        window.Count.ShouldBe(4);
        b.Select(r => r.After).ShouldBe(["\"Wölfe\"", "\"Other\""], ignoreOrder: true);
        (await f.Log.Query(new AuditQuery { After = "x" }, f.As("r"), Ct)).IsFailure.ShouldBeTrue();
        (await f.Log.Query(new AuditQuery { Limit = 201 }, f.As("r"), Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task A_rebuild_with_a_new_version_writes_the_same_rows()
    {
        var f = new Fixture();
        var child = Child.Register(NewChild(f.Time), "Mia", "Bären");
        await f.Commit(child);
        child.Rename("Mila").ShouldBe(Result.Success());
        await f.Commit(child);
        var first = await f.All();

        var rebuilt = new AuditHost(new AuditRegistry { Version = 2 }.Add<Child>(), f.Store, f.Events, f.Codec, f.Keys, f.Time);
        await rebuilt.CatchUp(Ct);
        var second = (await new AuditLog(rebuilt, f.Keys).Query(new AuditQuery(), f.As("r"), Ct)).GetValueOrThrow().Items;

        second.Select(r => (r.Id, r.Field, r.Before, r.After)).ShouldBe(first.Select(r => (r.Id, r.Field, r.Before, r.After)));
    }
}
