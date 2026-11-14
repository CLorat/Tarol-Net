using Tarol.Primitives.Tests.Galleries;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests;

/// <summary>Replay is strict, and so is the live path (one dispatch): F-7, F-14, I-4, I-5, I-9.</summary>
public class StrictReplayTests
{
    private static (Gallery Gallery, StreamEvent[] History, Room Room, Artwork Artwork) Built()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        var artwork = room.Hang("Rain");
        artwork.Reprice(5m);
        return (gallery, gallery.UncommittedChanges.ToArray(), room, artwork);
    }

    [Fact]
    public void A_Gap_Throws()
    {
        var (gallery, history, _, _) = Built();
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, [history[0], history[2]])).Message.ShouldContain("out of order");
    }

    [Fact]
    public void A_History_That_Does_Not_Start_At_One_Throws()
    {
        var (gallery, history, _, _) = Built();
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, history.AsSpan(1)));
    }

    [Fact]
    public void A_Root_Event_Of_Another_Aggregate_Throws()
    {
        var (_, history, _, _) = Built();
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(Id<Gallery>.New(), history)).Message.ShouldContain("belongs to");
    }

    [Fact]
    public void A_Child_Created_Under_Another_Root_Throws()
    {
        var (gallery, history, _, _) = Built();
        StreamEvent[] forged = [history[0], history[1] with { ParentId = Guid.NewGuid() }];
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, forged)).Message.ShouldContain("owner");
    }

    [Fact]
    public void An_Update_Of_An_Unknown_Entity_Throws()
    {
        var (gallery, history, _, _) = Built();
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, [history[0], history[3] with { Version = 2 }])).Message.ShouldContain("no such entity");
    }

    [Fact]
    public void A_Delete_Of_An_Unknown_Entity_Throws()
    {
        var (gallery, history, room, _) = Built();
        StreamEvent[] forged = [history[0], new(2, room.Id.Value, Guid.Empty, new Room.Closed())];
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, forged));
    }

    [Fact]
    public void A_Duplicate_Create_Throws()
    {
        var (gallery, history, _, _) = Built();
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, [history[0], history[1], history[1] with { Version = 3 }])).Message.ShouldContain("second time");
    }

    [Fact]
    public void An_Event_Of_Another_Aggregate_Type_Throws()
    {
        var (gallery, history, _, _) = Built();
        StreamEvent[] forged = [history[0], new(2, gallery.Id.Value, Guid.Empty, new Orders.Order.OrderPlaced())];
        Should.Throw<InvalidOperationException>(() => Gallery.Rehydrate(gallery.Id, forged)).Message.ShouldContain("not an event of aggregate Gallery");
    }

    [Fact]
    public void A_Null_Event_Throws()
    {
        var (gallery, history, _, _) = Built();
        Should.Throw<ArgumentNullException>(() => Gallery.Rehydrate(gallery.Id, [history[0], new(2, gallery.Id.Value, Guid.Empty, null!)]));
    }
}
