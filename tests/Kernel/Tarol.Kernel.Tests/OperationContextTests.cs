using Microsoft.Extensions.Time.Testing;
using Tarol.Options;
using Tarol.StronglyTypedId;

namespace Tarol.Kernel.Tests;

public class OperationContextTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Id<ITenant> Acme = Id<ITenant>.New();

    [Fact]
    public void Start_Should_CreateACorrelationIdFromTheClock()
    {
        var clock = new FakeTimeProvider(Start);

        var context = OperationContext.Start(Actor.User("alice"), Acme, clock);

        context.Actor.ShouldBe(Actor.User("alice"));
        context.Tenant.ShouldBe(Option.Some(Acme));
        context.CausationId.IsNone.ShouldBeTrue();
        context.CorrelationId.IsEmpty.ShouldBeFalse();
        context.CorrelationId.ShouldNotBe(OperationContext.Start(Actor.User("alice"), Acme, clock).CorrelationId);
        UuidV7Time(context.CorrelationId.Value).ShouldBe(Start); // the injected clock, not the system clock
    }

    [Fact]
    public void Start_WithoutTenant_Should_HaveNone()
        => OperationContext.Start(Actor.Anonymous, Option.None, new FakeTimeProvider(Start)).Tenant.IsNone.ShouldBeTrue();

    [Fact]
    public void CausedBy_Should_KeepTheConversation()
    {
        var context = OperationContext.Start(Actor.User("alice"), Acme, new FakeTimeProvider(Start));
        var message = Id<ICausation>.New();

        var next = context.CausedBy(message);

        next.CorrelationId.ShouldBe(context.CorrelationId);
        next.CausationId.ShouldBe(Option.Some(message));
        next.Actor.ShouldBe(context.Actor);
    }

    [Fact]
    public void AsSystem_Should_ActOnBehalfOfTheUser()
    {
        var context = OperationContext.Start(Actor.User("alice"), Acme, new FakeTimeProvider(Start)).CausedBy(Id<ICausation>.New());

        var saga = context.AsSystem("Sagas.AuctionTimeout");

        saga.Actor.ShouldBe(Actor.System("Sagas.AuctionTimeout", "alice"));
        (saga with { Actor = context.Actor }).ShouldBe(context); // tenant, correlation and causation are kept
    }

    [Fact]
    public void AsSystem_Should_KeepTheOriginalUser_AcrossSystemHops()
        => OperationContext.Start(Actor.User("alice"), Acme, new FakeTimeProvider(Start))
            .AsSystem("Sagas.Checkout").AsSystem("Outbox").Actor.ShouldBe(Actor.System("Outbox", "alice"));

    [Theory]
    [InlineData(ActorKind.Anonymous)]
    [InlineData(ActorKind.Service)]
    public void AsSystem_Should_ActForNobody_WhenTheCallerIsNoUser(ActorKind kind)
        => new OperationContext(new Actor(kind, "x"), Option.None, Id<ICorrelation>.New(), Option.None)
            .AsSystem("Job").Actor.OnBehalfOf.IsNone.ShouldBeTrue();

    [Fact]
    public void NullArguments_Should_Throw()
    {
        Should.Throw<ArgumentNullException>(() => new OperationContext(null!, Option.None, Id<ICorrelation>.New(), Option.None));
        Should.Throw<ArgumentNullException>(() => OperationContext.Start(null!, Option.None, TimeProvider.System));
        Should.Throw<ArgumentNullException>(() => OperationContext.Start(Actor.Anonymous, Option.None, null!));
    }

    // The 48-bit Unix millisecond timestamp of a UUIDv7 (RFC 9562 §5.7).
    private static DateTimeOffset UuidV7Time(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _).ShouldBeTrue();
        long ms = 0;
        for (var i = 0; i < 6; i++)
        {
            ms = (ms << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
