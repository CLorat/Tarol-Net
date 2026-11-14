using Tarol.Primitives.Tests.Fixtures;

namespace Tarol.Primitives.Tests;

/// <summary>F-9: an On handler that throws poisons the instance; it must be discarded.</summary>
public class FailureTests
{
    [Fact]
    public void A_Throwing_Handler_Should_Poison_The_Aggregate()
    {
        var faulty = Faulty.Create();
        faulty.Ok();

        Should.Throw<InvalidOperationException>(() => faulty.Fail()).Message.ShouldBe("boom");

        faulty.IsBroken.ShouldBeTrue();
        faulty.Applied.ShouldBe(2);          // half-applied: the mutation before the throw stays
        faulty.Version.ShouldBe(1);          // the event was not recorded
        faulty.UncommittedChanges.Length.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => faulty.Ok()).Message.ShouldContain("broken");
        Should.Throw<InvalidOperationException>(() => AggregateChanges.Prepare(faulty)).Message.ShouldContain("broken");
    }

    [Fact]
    public void Raising_Inside_A_Handler_Throws_And_Poisons()
    {
        var faulty = Faulty.Create();

        Should.Throw<InvalidOperationException>(() => faulty.Nested()).Message.ShouldContain("being applied");
        faulty.IsBroken.ShouldBeTrue();
    }

    [Fact]
    public void A_Throwing_Handler_On_Replay_Propagates()
    {
        var faulty = Faulty.Create();
        StreamEvent[] history = [new(1, faulty.Id.Value, Guid.Empty, new Faulty.Throws())];

        Should.Throw<InvalidOperationException>(() => Faulty.Rehydrate(faulty.Id, history)).Message.ShouldBe("boom");
    }
}
