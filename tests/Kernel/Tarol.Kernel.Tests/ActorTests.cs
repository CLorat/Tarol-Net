using Tarol.Options;

namespace Tarol.Kernel.Tests;

public class ActorTests
{
    [Fact]
    public void Factories_Should_SetKindAndId()
    {
        Actor.User("alice").ShouldBe(new Actor(ActorKind.User, "alice"));
        Actor.Service("billing-api").Kind.ShouldBe(ActorKind.Service);
        Actor.System("Sagas.AuctionTimeout").OnBehalfOf.IsNone.ShouldBeTrue();
        Actor.System("Sagas.AuctionTimeout", "alice").OnBehalfOf.ShouldBe(Option.Some("alice"));
        Actor.Anonymous.ShouldBe(new Actor(ActorKind.Anonymous, "anonymous"));
    }

    [Fact] // K-5: the identity provider's subject is a string, not a Guid
    public void User_Should_Accept_NonGuidSubjects()
    {
        Actor.User("auth0|5f7c8ec7c33c6c004bbafe82").Id.ShouldBe("auth0|5f7c8ec7c33c6c004bbafe82");
        Actor.User("AAAAAAAAAAAAAAAAAAAAAIkzqFVrSaSaFHy782bbtaQ").Kind.ShouldBe(ActorKind.User);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Id_Should_NotBeEmpty(string id)
        => Should.Throw<ArgumentException>(() => Actor.User(id));

    [Fact]
    public void Null_Id_Should_Throw() => Should.Throw<ArgumentNullException>(() => Actor.User(null!));

    [Fact]
    public void OnBehalfOf_Should_OnlyBeAllowed_ForSystem()
    {
        Should.Throw<ArgumentException>(() => new Actor(ActorKind.User, "alice", "bob"));
        Should.Throw<ArgumentException>(() => new Actor(ActorKind.Service, "api", "bob"));
        Should.Throw<ArgumentException>(() => Actor.System("job", " "));
    }

    [Fact]
    public void UnknownKind_Should_Throw() => Should.Throw<ArgumentOutOfRangeException>(() => new Actor((ActorKind)42, "x"));

    [Fact]
    public void ToString_Should_ShowKindIdAndPrincipal()
    {
        Actor.User("alice").ToString().ShouldBe("User:alice");
        Actor.System("Sagas.X", "alice").ToString().ShouldBe("System:Sagas.X (for alice)");
    }
}
