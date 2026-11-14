using Tarol.Primitives.Tests.Galleries;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests;

public class AggregateRootTests
{
    [Fact]
    public void Raise_Should_Apply_Version_And_Record_The_Event()
    {
        var gallery = Gallery.Open("Tate");

        gallery.Name.ShouldBe("Tate");
        gallery.Version.ShouldBe(1);
        gallery.UncommittedChanges.Length.ShouldBe(1);
        gallery.UncommittedChanges[0].ShouldBe(new StreamEvent(1, gallery.Id.Value, Guid.Empty, new Gallery.Opened("Tate")));
    }

    [Fact]
    public void A_Failing_Guard_Should_Return_Its_Error_And_Raise_Nothing()
    {
        var gallery = Gallery.Open("Tate");
        gallery.Tag("modern").IsSuccess.ShouldBeTrue();

        gallery.Tag("modern").ShouldBe(Result.Failure(Error.Conflict("Gallery.AlreadyTagged")));
        gallery.Version.ShouldBe(2);
    }

    [Fact]
    public void Committed_Should_Clear_The_Changes_But_Keep_The_Version()
    {
        var gallery = Gallery.Open("Tate");
        AggregateChanges.Committed(gallery);

        gallery.UncommittedChanges.IsEmpty.ShouldBeTrue();
        gallery.Version.ShouldBe(1);
        gallery.Tag("x").IsSuccess.ShouldBeTrue();
        gallery.UncommittedChanges[0].Version.ShouldBe(2);
    }

    [Fact]
    public void The_Root_Converts_To_Its_Id()
    {
        var gallery = Gallery.Open("Tate");
        Id<Gallery> id = gallery;
        id.ShouldBe(gallery.Id);
    }

    [Fact]
    public void Rehydrate_Should_Rebuild_The_Whole_Tree_Without_Uncommitted_Changes()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        room.Hang("Rain").Reprice(1000m);
        gallery.Tag("modern");

        var replayed = Gallery.Rehydrate(gallery.Id, gallery.UncommittedChanges);

        replayed.UncommittedChanges.IsEmpty.ShouldBeTrue();
        replayed.Version.ShouldBe(gallery.Version);
        replayed.Name.ShouldBe("Tate");
        replayed.Tags.ShouldBe(["modern"]);
        replayed.Rooms.Single().Title.ShouldBe("Turner");
        replayed.Rooms.Single().TotalValue.ShouldBe(1000m);
        replayed.Rooms.Single().Artworks.Single().Price.ShouldBe(1000m);
        replayed.ArtworkCount.ShouldBe(1);
    }

    [Fact]
    public void Rehydrate_Should_Continue_The_Version()
    {
        var gallery = Gallery.Open("Tate");
        var replayed = Gallery.Rehydrate(gallery.Id, gallery.UncommittedChanges);

        replayed.Tag("x");

        replayed.UncommittedChanges[0].Version.ShouldBe(2);
    }
}
