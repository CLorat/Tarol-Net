using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Caching;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.DataProtection.Tests;

/// <summary>Step 16C on the in-memory stack: erasure, export, retention (data-protection.md §6.4).</summary>
public sealed class DataSubjectsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Customer Ada, Order Order, Customer Bob)> Seed(App app)
    {
        var ada = Customer.Register(Id<Customer>.New(), "Ada", "ada@example.org", "GB");
        await app.Commit(ada);
        var order = Order.Place(Id<Order>.New(), ada.Id.Value, "Ada, 1 Main St", 42m);
        await app.Commit(order);
        var bob = Customer.Register(Id<Customer>.New(), "Bob", "bob@example.org", "DE");
        await app.Commit(bob);
        await app.Project();
        return (ada, order, bob);
    }

    [Fact]
    public async Task An_Erasure_Forgets_The_Subject_In_The_Store_Both_Projections_The_Cache_And_The_Lookups_And_Audits_It()
    {
        await using var app = new App();
        var (ada, order, bob) = await Seed(app);
        (await app.Row<CustomerRow>(ada.Id.Value))!.Email.ShouldBe("ada@example.org");
        var page = await app.ReadModels.Store<CustomerRow>().Search(new ListQuery(app.Tenant), Ct); // cached now
        page.TryGetValue(out Page<CustomerRow>? before).ShouldBeTrue();
        before.Items.Select(static r => r.Name).ShouldContain("Ada");

        var erasure = await app.Subjects.EraseSubject(app.Tenant, ada.Id.Value, "operator", Ct);

        erasure.AlreadyErased.ShouldBeFalse();
        erasure.Streams.Select(static s => s.StreamType).ShouldBe(["customer", "order"], ignoreOrder: true);
        var loaded = (await app.Store.Load(ada.Id, app.Context, Ct)).GetValueOrThrow();
        (loaded.Name, loaded.Email, loaded.Country).ShouldBe((null, null, "GB"));
        (await app.Row<CustomerRow>(ada.Id.Value)).ShouldBe(new CustomerRow(ada.Id.Value, null, null, "GB"));
        (await app.Row<OrderRow>(order.Id.Value)).ShouldBe(new OrderRow(order.Id.Value, ada.Id.Value, null, 42m));
        (await app.Row<CustomerRow>(bob.Id.Value))!.Name.ShouldBe("Bob");
        (await app.ReadModels.Store<CustomerRow>().Search(new ListQuery(app.Tenant), Ct)).TryGetValue(out Page<CustomerRow>? after).ShouldBeTrue();
        after.Items.Select(static r => r.Name).ShouldNotContain("Ada");                                   // the cached page is gone
        (await app.Store.Find<Customer>("email", "ada@example.org", app.Context, Ct)).IsNone.ShouldBeTrue();
        (await app.Store.Find<Customer>("email", "bob@example.org", app.Context, Ct)).IsSome.ShouldBeTrue();
        app.Memory.AuditRows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            a => a.Actor.ShouldBe("operator"),
            a => a.Action.ShouldBe("erase"),
            a => a.Target.ShouldBe($"subject:{ada.Id.Value}"),
            a => a.Detail!.ShouldNotContain("Ada"));
    }

    [Fact]
    public async Task A_Second_Erasure_Runs_The_Forgetting_Again()
    {
        await using var app = new App();
        var (ada, _, _) = await Seed(app);
        await app.Subjects.EraseSubject(app.Tenant, ada.Id.Value, "operator", Ct);
        (await app.Subjects.EraseSubject(app.Tenant, ada.Id.Value, "operator", Ct)).AlreadyErased.ShouldBeTrue();
        app.Memory.AuditRows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task New_Personal_Data_For_An_Erased_Subject_Is_Refused()
    {
        await using var app = new App();
        var (ada, _, _) = await Seed(app);
        await app.Subjects.EraseSubject(app.Tenant, ada.Id.Value, "operator", Ct);
        var loaded = (await app.Store.Load(ada.Id, app.Context, Ct)).GetValueOrThrow();
        loaded.Rename("Ada again");
        (await app.Store.Append(loaded, 1, app.Context, Option.None, Ct)).TryGetValue(out Violation? refused).ShouldBeTrue();
        refused.ShouldBeOfType<Error>().Code.ShouldBe(DataProtectionErrors.SubjectErasedCode);
    }

    [Fact]
    public async Task An_Export_Holds_The_Subjects_Events_And_State_And_Not_Other_Subjects_Data()
    {
        await using var app = new App();
        var (ada, order, bob) = await Seed(app);
        var bobsOrder = Order.Place(Id<Order>.New(), bob.Id.Value, "Bob, 2 Side St", 5m);
        await app.Commit(bobsOrder);

        var export = await app.Subjects.Export(app.Tenant, ada.Id.Value, "operator", Ct);

        var document = JsonElement.Parse(export.Utf8Json.Span);
        document.GetProperty("subject").GetGuid().ShouldBe(ada.Id.Value);
        var events = document.GetProperty("events").EnumerateArray().ToArray();
        events.Select(static e => e.GetProperty("streamType").GetString()).ShouldBe(["customer", "order"]);
        events[0].GetProperty("data").GetProperty("email").GetString().ShouldBe("ada@example.org");
        events[1].GetProperty("data").GetProperty("shipTo").GetString().ShouldBe("Ada, 1 Main St");
        events[1].GetProperty("streamId").GetGuid().ShouldBe(order.Id.Value);
        document.GetProperty("states").EnumerateArray().Single().GetProperty("state").GetProperty("name").GetString().ShouldBe("Ada");
        var text = System.Text.Encoding.UTF8.GetString(export.Utf8Json.Span);
        text.ShouldNotContain("Bob");
        app.Memory.AuditRows.ShouldHaveSingleItem().Action.ShouldBe("export");
    }

    [Fact]
    public async Task A_Tenant_Erasure_Forgets_Every_Subject_Of_The_Tenant()
    {
        await using var app = new App();
        var (ada, order, bob) = await Seed(app);

        await app.Subjects.EraseTenant(app.Tenant, "operator", Ct);

        (await app.Row<CustomerRow>(ada.Id.Value))!.Name.ShouldBeNull();
        (await app.Row<CustomerRow>(bob.Id.Value))!.Name.ShouldBeNull();
        (await app.Row<OrderRow>(order.Id.Value))!.ShipTo.ShouldBeNull();
        app.Memory.AuditRows.ShouldHaveSingleItem().Target.ShouldBe($"tenant:{app.Tenant}");
    }

    [Fact]
    public async Task Retention_Deletes_Expired_Streams_And_Their_Rows_With_The_Configured_Days()
    {
        await using var app = new App(o => o.Retention["order"] = 10);
        var (ada, order, _) = await Seed(app);
        app.Time.Advance(TimeSpan.FromDays(11));
        var fresh = Order.Place(Id<Order>.New(), ada.Id.Value, "x", 1m);
        await app.Commit(fresh);
        await app.Project();
        var task = app.Services.GetServices<MaintenanceTask>().Single(static t => t.Name == "tarol.retention");

        (await task.Run(app.Services, app.Time.GetUtcNow(), Ct)).ShouldBe(1);       // the order (10 days); customers keep 30

        (await app.Store.Load(order.Id, app.Context, Ct)).IsNone.ShouldBeTrue();
        (await app.Row<OrderRow>(order.Id.Value)).ShouldBeNull();
        (await app.Row<OrderRow>(fresh.Id.Value)).ShouldNotBeNull();
        (await app.Row<CustomerRow>(ada.Id.Value)).ShouldNotBeNull();
        app.Time.Advance(TimeSpan.FromDays(20));
        (await task.Run(app.Services, app.Time.GetUtcNow(), Ct)).ShouldBe(3);       // [Retention(Days = 30)] on Customer, the fresh order
        (await app.Row<CustomerRow>(ada.Id.Value)).ShouldBeNull();
        app.Memory.AuditRows.Select(static a => (a.Action, a.Target)).ShouldBe([("retention", "stream-type:order"), ("retention", "stream-type:customer"), ("retention", "stream-type:order")], ignoreOrder: true);
    }

    [Fact]
    public async Task Configuration_Overrides_The_Retention_And_Turns_It_Off()
    {
        await using var app = new App(configuration: new Dictionary<string, string?>
        {
            ["Tarol:DataProtection:MasterKey"] = App.MasterKey,
            ["Tarol:Retention:customer:Days"] = "0",
            ["Tarol:Retention:order:Days"] = "5",
        });
        var policy = app.Services.GetRequiredService<RetentionPolicy>();
        policy.TryGetDays("customer", out _).ShouldBeFalse();
        policy.TryGetDays("order", out var days).ShouldBeTrue();
        days.ShouldBe(5);
    }

    [Fact]
    public async Task A_Failing_Participant_Does_Not_Stop_The_Others_And_The_Erasure_Says_It_Is_Incomplete()
    {
        await using var app = new App();
        var keys = app.Services.GetRequiredService<IKeyStore>();
        var subjects = new DataSubjects(keys, app.Memory, [new Failing(), .. app.Services.GetServices<ErasureParticipant>()],
            app.Services.GetRequiredService<EventCodec>().Options, app.Time);
        var (ada, _, _) = await Seed(app);

        var incomplete = await Should.ThrowAsync<ErasureIncompleteException>(async () => await subjects.EraseSubject(app.Tenant, ada.Id.Value, "operator", Ct));

        incomplete.Failed.ShouldBe(["failing"]);
        (await app.Row<CustomerRow>(ada.Id.Value))!.Name.ShouldBeNull();       // the others ran
        app.Memory.AuditRows.Single().Detail!.ShouldContain("\"failed\":[\"failing\"]");
    }

    [Fact]
    public async Task The_Erasure_Signal_Evicts_The_Page_Cache_In_Every_Process()
    {
        await using var app = new App();
        var (ada, _, _) = await Seed(app);
        foreach (var hosted in app.Services.GetServices<IHostedService>().Where(static h => h.GetType().Name == "ErasureSignals"))
            await hosted.StartAsync(Ct);
        _ = await app.ReadModels.Store<CustomerRow>().Search(new ListQuery(app.Tenant), Ct);
        var cache = (MemoryPageCache)app.Services.GetRequiredService<IPageCache>();
        cache.Count.ShouldBe(1);

        // another process erased: only the key store's signal arrives here
        await app.Services.GetRequiredService<IKeyStore>().EraseSubject(app.Tenant, ada.Id.Value, Ct);

        cache.Count.ShouldBe(0);
    }

    [Fact]
    public void A_Missing_Master_Key_Says_Where_To_Put_It()
    {
        using var services = new App(configuration: new Dictionary<string, string?>()).Services;
        Should.Throw<InvalidOperationException>(() => services.GetRequiredService<IKeyEncryptionKey>()).Message.ShouldContain("Tarol:DataProtection:MasterKey");
    }

    [Fact]
    public void Invalid_Options_Are_Refused()
    {
        using var services = new App(o => o.RetentionBatch = 0).Services;
        Should.Throw<ArgumentOutOfRangeException>(() => services.GetRequiredService<DataProtectionOptions>());
    }

    private sealed class Failing : ErasureParticipant
    {
        public override string Name => "failing";

        public override ValueTask Forget(Guid tenant, Guid subject, IReadOnlyList<StreamRef> streams, CancellationToken cancellationToken)
            => throw new InvalidOperationException("down");
    }
}
