using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tarol.Changes;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Security;
using Tarol.StronglyTypedId;

namespace Tarol.Audit.Tests;

/// <summary>AddAudit (TN-64): the host, the log, the access observer (AU-5), tenant erasure and the own retention (AU-4).</summary>
public sealed class WiringTests
{
    private static readonly Id<ITenant> Tenant = Id<ITenant>.From(Guid.Parse("0196f000-0000-7000-8000-0000000000aa"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Provider(int? retentionDays = null)
    {
        var services = new ServiceCollection();
        var settings = new Dictionary<string, string?>();
        if (retentionDays is { } days)
            settings["Tarol:Audit:RetentionDays"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddTarol(AuditJson.Default, codec => codec.Add<Child>()).UseInMemoryStore().AddAudit(a => a.Add<Child>()).UseInMemoryAudit();
        return services.BuildServiceProvider();
    }

    private static AccessRecord Access(Actor actor, AccessDecision decision, DateTimeOffset at)
        => new(actor, Tenant, "Read<Child>", "Child", Guid.CreateVersion7(), "ChildHealth", decision, decision == AccessDecision.Denied ? "Auth.Forbidden" : null, at);

    [Fact]
    public async Task AddAudit_feeds_the_log_and_registers_the_host_the_observer_and_the_eraser()
    {
        await using var provider = Provider();
        var store = provider.GetRequiredService<IAggregateStore>();
        var context = OperationContext.Start(Actor.User("anna"), Tenant, TimeProvider.System);
        (await store.Append(Child.Register(Id<Child>.New(), "Mia", "Bären"), 0, context, Option.None, Ct)).IsSuccess.ShouldBeTrue();

        await provider.GetRequiredService<AuditHost>().CatchUp(Ct);
        var page = (await provider.GetRequiredService<IAuditLog>().Query(new AuditQuery(), context, Ct)).GetValueOrThrow();

        page.Items.Count.ShouldBe(2);
        provider.GetServices<ProjectionHost>().OfType<AuditHost>().ShouldHaveSingleItem();
        provider.GetServices<IHostedService>().OfType<AuditHost>().ShouldHaveSingleItem();
        provider.GetServices<ErasureParticipant>().Select(p => p.Name).ShouldContain("audit");
        provider.GetServices<MaintenanceTask>().Select(t => t.Name).ShouldContain(AuditTarolBuilderExtensions.PurgeJob);
    }

    [Fact]
    public async Task Sensitive_reads_and_denials_of_signed_in_callers_are_rows_anonymous_ones_are_not()
    {
        await using var provider = Provider();
        var observer = provider.GetServices<IAccessObserver>().ShouldHaveSingleItem();
        var now = TimeProvider.System.GetUtcNow();

        await observer.Observed(Access(Actor.User("anna"), AccessDecision.Allowed, now), Ct);
        await observer.Observed(Access(Actor.User("ben"), AccessDecision.Denied, now.AddSeconds(1)), Ct);
        await observer.Observed(Access(Actor.Anonymous, AccessDecision.Denied, now.AddSeconds(2)), Ct);

        var rows = (await provider.GetRequiredService<IAuditLog>().Query(new AuditQuery(), OperationContext.Start(Actor.User("r"), Tenant, TimeProvider.System), Ct)).GetValueOrThrow().Items;
        rows.Select(r => (r.Kind, r.Actor.Id, r.View, r.Code)).ShouldBe([(AuditKind.Denied, "ben", "ChildHealth", "Auth.Forbidden"), (AuditKind.Read, "anna", "ChildHealth", null)]);
        (await provider.GetRequiredService<IAuditLog>().Query(new AuditQuery { Kind = AuditKind.Read }, OperationContext.Start(Actor.User("r"), Tenant, TimeProvider.System), Ct))
            .GetValueOrThrow().Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Tenant_erasure_deletes_the_rows_and_the_retention_purges_old_ones()
    {
        await using var provider = Provider(retentionDays: 30);
        var observer = provider.GetServices<IAccessObserver>().ShouldHaveSingleItem();
        var now = TimeProvider.System.GetUtcNow();
        await observer.Observed(Access(Actor.User("anna"), AccessDecision.Allowed, now.AddDays(-40)), Ct);
        await observer.Observed(Access(Actor.User("anna"), AccessDecision.Allowed, now), Ct);
        var log = provider.GetRequiredService<IAuditLog>();
        var reader = OperationContext.Start(Actor.User("r"), Tenant, TimeProvider.System);

        var purged = await provider.GetServices<MaintenanceTask>().Single(t => t.Name == AuditTarolBuilderExtensions.PurgeJob).Run(provider, now, Ct);
        var kept = (await log.Query(new AuditQuery(), reader, Ct)).GetValueOrThrow().Items.Count;
        await provider.GetServices<ErasureParticipant>().Single(p => p.Name == "audit").ForgetTenant(Tenant.Value, Ct);
        var left = (await log.Query(new AuditQuery(), reader, Ct)).GetValueOrThrow().Items.Count;

        (purged, kept, left).ShouldBe((1, 1, 0));
    }

    [Fact]
    public void AddAudit_without_an_aggregate_or_twice_throws_and_without_a_store_fails_when_resolved()
    {
        var services = new ServiceCollection();
        var tarol = services.AddTarol(AuditJson.Default, codec => codec.Add<Child>()).UseInMemoryStore();
        Should.Throw<InvalidOperationException>(() => tarol.AddAudit(_ => { }));
        tarol.AddAudit(a => a.Add<Child>());
        Should.Throw<InvalidOperationException>(() => tarol.AddAudit(a => a.Add<Child>()));
        using var provider = services.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<AuditHost>()).Message.ShouldContain("UseInMemoryAudit");
    }
}
