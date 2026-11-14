using Tarol.Primitives.Tests.Galleries;

namespace Tarol.Primitives.Tests;

/// <summary>The generated stream type and event catalog (step 8, P-7 and P-13).</summary>
public class EventCatalogTests
{
    [Fact]
    public void Stream_Type_Is_The_Snake_Cased_Name_Or_The_Pin()
    {
        Gallery.StreamType.ShouldBe("gallery");
        Gallery.EventTypes.StreamType.ShouldBe("gallery");
        Orders.Order.StreamType.ShouldBe("test_order");
    }

    [Fact]
    public void Catalog_Lists_Every_Concrete_Event_With_Its_Event_Type()
    {
        var entries = Gallery.EventTypes.Events;
        entries.Select(static e => e.ClrType).ShouldBe(
            [typeof(Artwork.Hung), typeof(Artwork.Removed), typeof(Artwork.Repriced), typeof(ArtworkRenamed),
             typeof(Gallery.Opened), typeof(Gallery.Tagged), typeof(Room.Added), typeof(Room.Closed)], ignoreOrder: true);
        entries.ShouldContain(new EventCatalogEntry(new EventType("gallery.artwork.renamed", 2), typeof(ArtworkRenamed)));
        entries.ShouldContain(new EventCatalogEntry(new EventType("gallery.artwork.repriced", 1), typeof(Artwork.Repriced)));
    }

    [Fact]
    public void Catalog_Agrees_With_EventTypeOf()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Turner").Hang("Rain").Reprice(5m);
        foreach (var change in gallery.UncommittedChanges)
            Gallery.EventTypes.Events.ShouldContain(new EventCatalogEntry(Gallery.EventTypeOf(change.Event), change.Event.GetType()));
    }

    [Fact]
    public void Catalog_Rejects_An_Empty_Stream_Type()
        => Should.Throw<ArgumentException>(() => new EventCatalog(" "));
}
