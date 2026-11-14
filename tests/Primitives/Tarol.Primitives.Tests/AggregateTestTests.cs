using Tarol.Primitives.Tests.Galleries;
using Tarol.Results;

namespace Tarol.Primitives.Tests;

public class AggregateTestTests
{
    [Fact]
    public void Given_When_Then_With_Events()
    {
        var outcome = AggregateTest.Given(Gallery.Open("Tate"))
            .And(static g => g.Tag("modern"))
            .When(static g => g.Tag("classic"));

        outcome.Result.IsSuccess.ShouldBeTrue();
        outcome.Events.ShouldBe([new Gallery.Tagged("classic")]);
        outcome.Aggregate.Tags.ShouldBe(["modern", "classic"]);
        outcome.Replay().Tags.ShouldBe(outcome.Aggregate.Tags);
    }

    [Fact]
    public void A_Failing_Behaviour_Raises_Nothing()
    {
        var outcome = AggregateTest.Given(Gallery.Open("Tate"))
            .And(static g => g.Tag("modern"))
            .When(static g => g.Tag("modern"));

        outcome.Result.ShouldBe(Result.Failure(Error.Conflict("Gallery.AlreadyTagged")));
        outcome.Events.ShouldBeEmpty();
    }

    [Fact]
    public void A_Broken_Invariant_Is_The_Outcome()
    {
        var test = AggregateTest.Given(Gallery.Open("Tate"));
        test.And(static g => { g.AddRoom("Turner").Hang("Rain"); return Result.Success(); });

        var outcome = test.When(static g => g.Rooms[0].Artworks[0].Reprice(-1));

        outcome.Result.ShouldBe(Result.Failure(Error.Conflict("Artwork.NegativePrice")));
        outcome.Events.ShouldBeEmpty();
    }

    [Fact]
    public void When_With_A_Value_Reports_The_Raised_Creation()
    {
        var outcome = AggregateTest.Given(Gallery.Open("Tate")).When(static g => Result.Success(g.AddRoom("Turner")));

        outcome.Events.ShouldBe([new Room.Added("Turner")]);
        outcome.History.Count.ShouldBe(2);
    }

    [Fact]
    public void A_Failing_Given_Step_Throws()
        => Should.Throw<InvalidOperationException>(() => AggregateTest.Given(Gallery.Open("Tate")).And(static g => g.Tag("a")).And(static g => g.Tag("a")));
}
