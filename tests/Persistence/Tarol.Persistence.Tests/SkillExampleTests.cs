using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Tests;

/// <summary>The example of skills/tarol-persistence/SKILL.md, verbatim: every API name in the skill compiles and runs.</summary>
public sealed class SkillExampleTests
{
    [Fact]
    public async Task The_skill_example_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        var time = new FakeTimeProvider(start);
        var store = new InMemoryAggregateStore(time, TestJson.Default.Options).DefineLookup<Account>("number", a => a.Number);
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(time), time);

        var account = Account.Open("A-1");                                    // static factory raised Opened
        (await store.Append(account, 0, context, Option.None, ct)).IsSuccess.ShouldBeTrue();
        AggregateChanges.Committed(account);

        var loaded = (await store.Load(account.Id, context, ct)).GetValueOrThrow();
        var expected = loaded.Version;                                        // 1
        loaded.Deposit(10).IsSuccess.ShouldBeTrue();
        AggregateChanges.Prepare(loaded).IsSuccess.ShouldBeTrue();
        var key = new IdempotencyKey(context.Tenant, "payments", "evt_1");
        (await store.Append(loaded, expected, context, key, ct)).IsSuccess.ShouldBeTrue();
        AggregateChanges.Committed(loaded);

        (await store.Seen(key, ct)).GetValueOrThrow().ShouldBe(new StoredOutcome(account.Id.Value, 2, 1));
        (await store.Find<Account>("number", "A-1", context, ct)).GetValueOrThrow().ShouldBe(account.Id);
        store.Recorded[^1].Context.ShouldBe(context);
    }

    [Fact]
    public async Task The_codec_example_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(time), time);

        var codec = new EventCodec(new JsonSerializerOptions(CodecJson.Default.Options))
            .Add<Subscription>()
            .Upcast<Subscription>(new CancelledV1ToV2());
        var lookups = new StoreLookups().Define<Subscription>("plan", s => s.Plan);
        var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Codec = codec, Lookups = lookups });

        var subscription = Subscription.Start("pro");
        subscription.Cancel("price");
        (await store.Append(subscription, 0, context, Option.None, ct)).IsSuccess.ShouldBeTrue();

        codec.Decode(Subscription.StreamType, new EventType("subscription.cancelled", 1), "{}"u8)
            .ShouldBe(new Subscription.Cancelled("unknown"));
        await foreach (var e in store.ReadAll(GlobalPosition.Start, 100, ct))
            e.StreamType.ShouldBe("subscription");
    }
}
