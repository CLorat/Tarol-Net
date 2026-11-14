using System.Text.Json;
using Tarol.Primitives.Tests.Fixtures;
using Tarol.Primitives.Tests.Galleries;

namespace Tarol.Primitives.Tests;

public class EventTests
{
    [Fact]
    public void Events_Are_Pure_Payload_With_Value_Equality()
    {
        new Artwork.Repriced(5m).ShouldBe(new Artwork.Repriced(5m));
        new Room.Closed().ShouldBe(new Room.Closed());

        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Turner").Hang("Rain").Reprice(5m);
        gallery.UncommittedChanges[3].Event.ShouldBe(new Artwork.Repriced(5m)); // no envelope or clock in the event
    }

    [Fact]
    public void Default_Aliases_Follow_The_Nesting()
    {
        Gallery.EventTypeOf(new Gallery.Opened("x")).ShouldBe(new EventType("gallery.opened", 1));
        Gallery.EventTypeOf(new Room.Added("x")).ShouldBe(new EventType("gallery.room.added", 1));
        Gallery.EventTypeOf(new Artwork.Repriced(1)).ShouldBe(new EventType("gallery.artwork.repriced", 1));
    }

    [Fact]
    public void EventTypeOf_Rejects_A_Foreign_Event()
        => Should.Throw<InvalidOperationException>(() => Gallery.EventTypeOf(new Board.Renamed(default)));

    [Fact]
    public void A_Same_Assembly_Json_Context_Round_Trips_Events()
    {
        var json = JsonSerializer.Serialize(new Artwork.Repriced(12.5m), TestJsonOptions.Info<Artwork.Repriced>());

        json.ShouldBe("""{"price":12.5}""");
        JsonSerializer.Deserialize(json, TestJsonOptions.Info<Artwork.Repriced>()).ShouldBe(new Artwork.Repriced(12.5m));
        JsonSerializer.Serialize(new Room.Closed(), TestJsonOptions.Info<Room.Closed>()).ShouldBe("{}");
    }

    [Fact]
    public void A_Stream_Survives_A_Json_Round_Trip_Of_Its_Payloads()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Turner");
        room.Hang("Rain").Reprice(3m);
        room.Close();

        var stored = gallery.UncommittedChanges.ToArray().Select(e => e with
        {
            Event = e.Event switch
            {
                Room.Added x => JsonSerializer.Deserialize(JsonSerializer.Serialize(x, TestJsonOptions.Info<Room.Added>()), TestJsonOptions.Info<Room.Added>())!,
                Room.Closed x => JsonSerializer.Deserialize(JsonSerializer.Serialize(x, TestJsonOptions.Info<Room.Closed>()), TestJsonOptions.Info<Room.Closed>())!,
                Artwork.Repriced x => JsonSerializer.Deserialize(JsonSerializer.Serialize(x, TestJsonOptions.Info<Artwork.Repriced>()), TestJsonOptions.Info<Artwork.Repriced>())!,
                var other => other,
            },
        }).ToArray();

        stored.ShouldBe(gallery.UncommittedChanges.ToArray());
        Gallery.Rehydrate(gallery.Id, stored).Rooms.ShouldBeEmpty();
    }
}
