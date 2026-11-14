using Tarol.Primitives.Tests.Galleries;
using Tarol.Results;

namespace Tarol.Primitives.Tests;

/// <summary>V-3: invariants run at the hand-off, on the root and on every entity, never on replay.</summary>
public class InvariantTests
{
    [Fact]
    public void Prepare_Should_Succeed_When_Every_Invariant_Holds()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Turner").Hang("Rain").Reprice(1);

        AggregateChanges.Prepare(gallery).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Prepare_Should_Return_An_Entity_Invariant_Failure_Unchanged()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Turner").Hang("Rain").Reprice(-1);

        AggregateChanges.Prepare(gallery).ShouldBe(Result.Failure(Error.Conflict("Artwork.NegativePrice")));
        gallery.UncommittedChanges.Length.ShouldBe(4); // nothing is rolled back: the pipeline discards the instance
    }

    [Fact]
    public void Prepare_Should_Return_A_Root_Invariant_Failure()
    {
        var gallery = Gallery.Open("Tate");
        for (var i = 0; i < 11; i++)
            gallery.Tag("t" + i);

        AggregateChanges.Prepare(gallery).ShouldBe(Result.Failure(Error.Conflict("Gallery.TooManyTags")));
    }

    [Fact]
    public void Prepare_Checks_Every_Entity_Not_Only_The_Changed_Ones()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Turner").Hang("Rain").Reprice(-1);
        var replayed = Gallery.Rehydrate(gallery.Id, gallery.UncommittedChanges);

        replayed.Tag("x");

        AggregateChanges.Prepare(replayed).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Replay_Never_Runs_Invariants()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Turner").Hang("Rain").Reprice(-1);

        Gallery.Rehydrate(gallery.Id, gallery.UncommittedChanges).Rooms[0].Artworks[0].Price.ShouldBe(-1);
    }

    [Fact]
    public void An_Aggregate_Without_Invariants_Prepares_Successfully()
        => AggregateChanges.Prepare(Orders.Order.Place()).IsSuccess.ShouldBeTrue();
}
