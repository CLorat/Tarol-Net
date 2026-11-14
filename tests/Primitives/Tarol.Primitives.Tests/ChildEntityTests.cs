using Tarol.Primitives.Tests.Galleries;
using Tarol.Primitives.Tests.Orders;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests;

public class ChildEntityTests
{
    [Fact]
    public void Create_Should_Return_The_Child_Registered_In_Set_And_Map()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        var artwork = room.Hang("Rain");

        gallery.Rooms.Single().ShouldBeSameAs(room);
        room.Artworks[artwork.Id].ShouldBeSameAs(artwork);
        ((IRegistry<Artwork>)gallery).TryFind(artwork.Id, out var found).ShouldBeTrue();
        found.ShouldBeSameAs(artwork);
        gallery.UncommittedChanges[2].ShouldBe(new StreamEvent(3, artwork.Id.Value, room.Id.Value, new Artwork.Hung("Rain")));
    }

    [Fact]
    public void Create_With_An_Explicit_Id_Should_Use_It()
    {
        var gallery = Gallery.Open("Tate");
        var id = Id<Room>.New();

        gallery.AddRoom("Turner", id).Id.ShouldBe(id);
    }

    [Fact]
    public void Handlers_Should_Run_Target_First_Then_Ancestors_Then_The_Root()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        room.Hang("A").Reprice(10m);
        room.Hang("B").Reprice(5m);

        room.TotalValue.ShouldBe(15m);    // Room.On(Artwork.Repriced) sees the artwork's updated price
        gallery.ArtworkCount.ShouldBe(2); // Gallery.On(Artwork.Hung) runs for a grandchild's creation
    }

    [Fact]
    public void An_Entity_Event_Should_Use_The_Top_Level_Event_With_A_Pinned_Alias()
    {
        var gallery = Gallery.Open("Tate");
        var artwork = gallery.AddRoom("Turner").Hang("Rain");

        artwork.Rename("Rain, Steam and Speed");

        artwork.Name.ShouldBe("Rain, Steam and Speed");
        Gallery.EventTypeOf(new ArtworkRenamed("x")).ShouldBe(new EventType("gallery.artwork.renamed", 2));
    }

    [Fact]
    public void Delete_Should_Cascade_To_Descendants()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        var artwork = room.Hang("Rain");

        room.Close();

        gallery.Rooms.ShouldBeEmpty();
        room.Artworks.ShouldBeEmpty();
        ((IRegistry<Room>)gallery).TryFind(room.Id, out _).ShouldBeFalse();
        ((IRegistry<Artwork>)gallery).TryFind(artwork.Id, out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => artwork.Reprice(1)); // a zombie cannot raise
        Gallery.Rehydrate(gallery.Id, gallery.UncommittedChanges).Rooms.ShouldBeEmpty();
    }

    [Fact]
    public void An_Entity_Should_Delete_Itself()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        var artwork = room.Hang("Rain");

        artwork.Remove();

        room.Artworks.ShouldBeEmpty();
        gallery.UncommittedChanges[^1].ShouldBe(new StreamEvent(4, artwork.Id.Value, Guid.Empty, new Artwork.Removed()));
    }

    [Fact]
    public void The_Parent_Can_Delete_By_Id()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");

        gallery.CloseRoom(room.Id).IsSuccess.ShouldBeTrue();
        gallery.CloseRoom(room.Id).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void EntityRef_Should_Hold_One_Child()
    {
        var order = Order.Place();
        var shipping = order.ShipTo("Main St 1");

        order.Shipping.Value.ShouldBeSameAs(shipping);
        Should.Throw<InvalidOperationException>(() => order.ShipTo("Elsewhere")).Message.ShouldContain("already holds one");
        order.Version.ShouldBe(2); // the failed creation recorded nothing … but the instance is broken now
        order.IsBroken.ShouldBeTrue();
    }

    [Fact]
    public void EntityRef_Should_Be_Cleared_By_The_Delete()
    {
        var order = Order.Place();
        order.ShipTo("Main St 1").Cancel();

        order.Shipping.HasValue.ShouldBeFalse();
        order.ShipTo("Elsewhere").Address.ShouldBe("Elsewhere");
        Order.Rehydrate(order.Id, order.UncommittedChanges).Shipping.Value!.Address.ShouldBe("Elsewhere");
    }

    [Fact]
    public void A_Child_With_Two_Owner_Types_Is_Created_In_The_Named_Owner()
    {
        var order = Order.Place();
        var line = order.Add("SKU-1", 2);
        var onOrder = order.Annotate("gift");
        var onLine = line.Annotate("fragile");

        order.Notes.Single().ShouldBeSameAs(onOrder);
        line.Notes.Single().ShouldBeSameAs(onLine);
        line.ConstructedByUser.ShouldBeTrue(); // the user's private (Id, Root) constructor was called

        line.Remove();
        ((IRegistry<Note>)order).TryFind(onLine.Id, out _).ShouldBeFalse();
        order.Notes.Single().ShouldBeSameAs(onOrder);

        onOrder.Delete();
        order.Notes.ShouldBeEmpty();

        var replayed = Order.Rehydrate(order.Id, order.UncommittedChanges);
        replayed.Lines.ShouldBeEmpty();
        replayed.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void An_EntitySet_Only_Accepts_An_Empty_Initializer()
        => Should.Throw<InvalidOperationException>(() => EntitySet.Create<Room>([null!]));
}
