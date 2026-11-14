using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence.Contract;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>The example of skills/tarol-persistence-npgsql/SKILL.md, verbatim between the markers.</summary>
[Trait("Category", "Integration")]
public sealed class SkillExampleTests(Postgres postgres)
{
    [Fact]
    public async Task The_skill_example_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = postgres.ConnectionString;

        // --- skill example
        var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
        var codec = new EventCodec(new JsonSerializerOptions(ContractJson.Default.Options)).Add<Member>().Add<Tally>();
        var lookups = new StoreLookups().Define<Member>("email", m => m.Email);
        var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions { Schema = "skill", Lookups = lookups });
        await store.EnsureSchema(ct);

        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
        var member = Member.Join(Id<Member>.New(), "a@x");
        (await store.Append(member, 0, context, Option.None, ct)).IsSuccess.ShouldBeTrue();
        AggregateChanges.Committed(member);

        var loaded = (await store.Load(member.Id, context, ct)).GetValueOrThrow();
        var expected = loaded.Version;
        loaded.Earn(5).IsSuccess.ShouldBeTrue();
        (await store.Append(loaded, expected, context, Option.None, ct)).IsSuccess.ShouldBeTrue();
        (await store.Find<Member>("email", "a@x", context, ct)).GetValueOrThrow().ShouldBe(member.Id);

        var checkpoint = GlobalPosition.Start;
        await foreach (var e in store.ReadAll(checkpoint, 500, ct))
            checkpoint = e.Position;                                       // persist it with the projection
        (await store.RebuildStates<Member>(ct)).ShouldBe(0);
        _ = await store.PurgeKeys(TimeProvider.System.GetUtcNow() - TimeSpan.FromDays(7), 1000, ct);
        // --- end

        await db.DisposeAsync();
    }
}
