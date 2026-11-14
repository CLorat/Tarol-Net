using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tarol.Options;
using Tarol.Primitives.Tests.Fixtures;
using Tarol.Primitives.Tests.Galleries;
using Tarol.Primitives.Tests.Nested;
using Tarol.Primitives.Tests.Orders;
using Tarol.Primitives.Tests.Showrooms;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests;

/// <summary>Stored state (part B): round trips, the JSON shape, schema changes, malformed states, restoring without events.</summary>
[Collection(StaticCounters.Name)]
public class StoredStateTests
{
    private static readonly JsonSerializerOptions Options = TestJsonOptions.Value;
    private static readonly DateTimeOffset At = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    private static Id<T> Fixed<T>(int n) => Id<T>.From(Guid.Parse($"01900000-0000-7000-8000-{n:D12}"));

    private static string Json<T>(T aggregate) where T : AggregateRoot<T>, IAggregateRoot<T>
        => Encoding.UTF8.GetString(AggregateState.Write(aggregate, Options));

    private static Option<T> Read<T>(Id<T> id, string json) where T : AggregateRoot<T>, IAggregateRoot<T>
        => AggregateState.Read(id, Encoding.UTF8.GetBytes(json), Options);

    // ── round trips: every test model ──

    [Fact]
    public void Gallery_Round_Trips()
    {
        var gallery = Gallery.Open("Tate");
        gallery.Tag("modern");
        var room = gallery.AddRoom("Turbine Hall");
        room.Hang("Weather").Reprice(10m);
        room.Hang("Shibboleth");
        gallery.AddRoom("Side").Close();
        AggregateTest.StoredStateMatchesReplay(gallery, Options);
    }

    [Fact]
    public void Orders_Round_Trip_With_An_EntityRef_Two_Owner_Types_And_A_User_Constructor()
    {
        var order = Order.Place();
        var line = order.Add("sku-1", 2);
        line.Annotate("fragile");
        order.Annotate("gift");
        order.ShipTo("Main St 1");
        var loaded = AggregateTest.StoredStateMatchesReplay(order, Options);

        loaded.Lines[0].ConstructedByUser.ShouldBeTrue();
        loaded.Lines[0].Notes[0].Text.ShouldBe("fragile");
        loaded.Notes[0].Text.ShouldBe("gift");
        loaded.Shipping.Value!.Address.ShouldBe("Main St 1");
    }

    [Fact]
    public void Nested_Types_Round_Trip()
    {
        var station = Space.Station.Launch();
        var pod = station.Dock("Harmony");
        pod.Repair(3);
        pod.Open(4);
        station.Dock("Unity").Undock();
        var loaded = AggregateTest.StoredStateMatchesReplay(station, Options);

        loaded.Pods.Single().Bay.Value!.Size.ShouldBe(4);
        loaded.Pods.Single().Hours.ShouldBe(3);
    }

    [Fact]
    public void A_Value_Object_Round_Trips_As_Its_Bare_Value()
    {
        var board = Board.Create();
        board.Rename("Kanban");
        AggregateTest.StoredStateMatchesReplay(board, Options);
        Json(board).ShouldContain("\"name\":\"Kanban\"");
    }

    [Fact]
    public void Every_Member_Kind_Round_Trips()
    {
        var showroom = Showrooms.Showroom.Open("Tate", At);
        showroom.Relabel("Modern");
        showroom.Tag("art");
        showroom.SetMotto("Look closer");
        showroom.Move("Millbank", "London");
        showroom.Count(1200);
        showroom.SetBudget(250_000.50m);
        var hall = showroom.AddHall("Turbine");
        hall.Light(Lighting.Dim);
        hall.Limit(400);
        var exhibit = hall.Hang("Weather", 1_000m);
        exhibit.Prefer(Lighting.Warm);
        exhibit.SetReserve(900m);
        exhibit.Annotate("on loan");
        hall.Annotate("busy");
        showroom.Appoint("Frances");
        _ = showroom.Summary;

        var loaded = AggregateTest.StoredStateMatchesReplay(showroom, Options);

        loaded.Motto.ShouldBe(new Option<string>("Look closer"));
        loaded.Address.ShouldBe(new Address("Millbank", "London"));
        loaded.Budget.ShouldBe(250_000.50m);
        loaded.Log.ShouldBe(["tag:art"]);
        loaded.Halls[0].Capacity.ShouldBe(new Option<int>(400));
        loaded.Halls[0].Exhibits[0].Preferred.ShouldBe(Lighting.Warm);
        loaded.Halls[0].Exhibits[0].FirstHall.ShouldBe(hall.Id);
        loaded.Summary.ShouldBe(showroom.Summary);
    }

    // ── the agreed JSON shape ──

    [Fact]
    public void The_State_Is_Queryable_Json_With_Property_Names_And_Nested_Children()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Hall", Fixed<Room>(1));
        room.Hang("Weather");

        var json = Json(gallery).Replace(gallery.Id.Value.ToString(), "G", StringComparison.Ordinal);
        var artwork = room.Artworks[0].Id.Value.ToString();
        json.Replace(artwork, "A", StringComparison.Ordinal).ShouldBe(
            $$"""{"id":"G","version":3,"schema":{{Gallery.StateSchema}},"artworkCount":1,"name":"Tate","tags":[],"rooms":[{"id":"01900000-0000-7000-8000-000000000001","title":"Hall","totalValue":0,"artworks":[{"id":"A","name":"Weather","price":0}]}]}""");
    }

    [Fact]
    public void An_Empty_EntityRef_And_None_Are_Written_As_Null()
    {
        var order = Order.Place();
        Json(order).ShouldContain("\"shipping\":null");

        var showroom = Showrooms.Showroom.Open("Bare", At);
        var json = Json(showroom);
        json.ShouldContain("\"motto\":null");
        json.ShouldContain("\"address\":null");
        json.ShouldContain("\"budget\":null");
        json.ShouldContain("\"curator\":null");
    }

    [Fact]
    public void Enums_Are_Written_By_Name_And_StateName_Pins_A_Name()
    {
        var showroom = Showrooms.Showroom.Open("Tate", At);
        showroom.AddHall("Turbine").Light(Lighting.Warm);
        showroom.Count(5);
        var json = Json(showroom);
        json.ShouldContain("\"lighting\":\"Warm\"");
        json.ShouldContain("\"humidity\":0.55");
        json.ShouldContain("\"visitors\":5");
        json.ShouldNotContain("visitorCount");
        json.ShouldNotContain("summary"); // [Transient]
    }

    [Fact]
    public void Key_Order_Does_Not_Matter_On_Read()
    {
        var showroom = Showrooms.Showroom.Open("Tate", At);
        showroom.AddHall("Turbine").Hang("Weather", 1m).Annotate("n");
        var json = Json(showroom);
        Json(Read(showroom.Id, JsonbOrder(json)).GetValueOrThrow()).ShouldBe(json);
    }

    // ── shape changes: None, then replay; the next write uses the current schema ──

    [Fact]
    public void An_Old_Schema_Reads_As_None_Then_The_Load_Replays()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Hall").Hang("Weather");
        var history = gallery.UncommittedChanges.ToArray();
        var old = Json(gallery).Replace($"\"schema\":{Gallery.StateSchema}", "\"schema\":7", StringComparison.Ordinal);

        Read(gallery.Id, old).IsNone.ShouldBeTrue();

        var replayed = Gallery.Rehydrate(gallery.Id, history);
        replayed.Tag("next");
        Json(replayed).ShouldContain($"\"schema\":{Gallery.StateSchema}"); // the next commit rewrites the row
    }

    [Fact]
    public void A_Schema_Mismatch_Skips_The_Object()
    {
        var gallery = Gallery.Open("Tate");
        var bytes = Encoding.UTF8.GetBytes("[" + Json(gallery).Replace($"\"schema\":{Gallery.StateSchema}", "\"schema\":7", StringComparison.Ordinal) + ",1]");
        var reader = new Utf8JsonReader(bytes);
        reader.Read(); // [
        reader.Read(); // {
        Gallery.ReadState(gallery.Id, ref reader, Options).IsNone.ShouldBeTrue();
        reader.Read().ShouldBeTrue();
        reader.GetInt32().ShouldBe(1);
    }

    [Fact]
    public void A_State_Without_Schema_Reads_As_None()
        => Read(Gallery.Open("Tate").Id, """{"id":"x","name":"Tate"}""").IsNone.ShouldBeTrue();

    // ── restoring without events ──

    [Fact]
    public void Restoring_Runs_No_Rule()
    {
        // stored when names could be 30 characters; today's rule is 1..10
        var board = Board.Create();
        var json = $$"""{"id":"{{board.Id.Value}}","version":1,"schema":{{Board.StateSchema}},"name":"An old, long board name"}""";
        var before = BoardName.Checks;

        var loaded = Read(board.Id, json).GetValueOrThrow();

        BoardName.Checks.ShouldBe(before);
        loaded.Name.Value.ShouldBe("An old, long board name");
        loaded.Version.ShouldBe(1);
        AggregateChanges.Prepare(loaded).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Restoring_Runs_No_On_Handler()
    {
        var leaky = Leaky.Create();
        leaky.Bump();
        var json = Json(leaky);
        var applied = Leaky.Applied;

        Read(leaky.Id, json).GetValueOrThrow().Count.ShouldBe(1);
        Leaky.Applied.ShouldBe(applied);
    }

    [Fact]
    public void A_Loaded_Aggregate_Continues_With_The_Same_Versions_And_Registry()
    {
        var gallery = Gallery.Open("Tate");
        var room = gallery.AddRoom("Hall");
        var artwork = room.Hang("Weather");
        var history = gallery.UncommittedChanges.ToArray();
        var loaded = Read(gallery.Id, Json(gallery)).GetValueOrThrow();

        loaded.Version.ShouldBe(3);
        loaded.UncommittedChanges.IsEmpty.ShouldBeTrue();
        ((IRegistry<Artwork>)loaded).TryFind(artwork.Id, out _).ShouldBeTrue();

        loaded.CloseRoom(room.Id).IsSuccess.ShouldBeTrue();
        loaded.UncommittedChanges[0].Version.ShouldBe(4);
        ((IRegistry<Artwork>)loaded).TryFind(artwork.Id, out _).ShouldBeFalse(); // cascade on a restored tree
        Json(Gallery.Rehydrate(gallery.Id, [.. history, .. loaded.UncommittedChanges])).ShouldBe(Json(loaded));
    }

    [Fact]
    public void Restoring_Onto_A_Used_Aggregate_Is_Impossible_By_Construction()
    {
        // ReadState always builds a new instance; a second read gives another one
        var gallery = Gallery.Open("Tate");
        var json = Json(gallery);
        Read(gallery.Id, json).GetValueOrThrow().ShouldNotBeSameAs(Read(gallery.Id, json).GetValueOrThrow());
    }

    // ── malformed states ──

    [Fact]
    public void A_State_Of_Another_Aggregate_Throws()
    {
        var gallery = Gallery.Open("Tate");
        Should.Throw<InvalidOperationException>(() => Read(Id<Gallery>.New(), Json(gallery))).Message.ShouldContain("belongs to Gallery");
    }

    [Fact]
    public void An_Unknown_Member_Throws()
    {
        var gallery = Gallery.Open("Tate");
        Should.Throw<JsonException>(() => Read(gallery.Id, Json(gallery).Replace("\"name\":", "\"title\":", StringComparison.Ordinal)))
            .Message.ShouldContain("unknown member \"title\"");
    }

    [Fact]
    public void A_Missing_Member_Throws()
    {
        var gallery = Gallery.Open("Tate");
        Should.Throw<JsonException>(() => Read(gallery.Id, Json(gallery).Replace("\"name\":\"Tate\",", "", StringComparison.Ordinal)))
            .Message.ShouldContain("lacks members");
    }

    [Fact]
    public void An_Unknown_Enum_Name_Throws()
    {
        var showroom = Showrooms.Showroom.Open("Tate", At);
        showroom.AddHall("Turbine").Light(Lighting.Dim);
        Should.Throw<JsonException>(() => Read(showroom.Id, Json(showroom).Replace("\"Dim\"", "\"Bright\"", StringComparison.Ordinal)))
            .Message.ShouldContain("Lighting \"Bright\"");
    }

    [Fact]
    public void An_Enum_Number_Reads()
    {
        var showroom = Showrooms.Showroom.Open("Tate", At);
        showroom.AddHall("Turbine").Light(Lighting.Dim);
        Read(showroom.Id, Json(showroom).Replace("\"Dim\"", "1", StringComparison.Ordinal)).GetValueOrThrow().Halls[0].Lighting.ShouldBe(Lighting.Warm);
    }

    [Fact]
    public void An_Entity_Twice_Throws()
    {
        var gallery = Gallery.Open("Tate");
        gallery.AddRoom("Hall");
        var json = Json(gallery);
        var room = JsonElement.Parse(json).GetProperty("rooms")[0].GetRawText();
        Should.Throw<InvalidOperationException>(() => Read(gallery.Id, json.Replace(room, room + "," + room, StringComparison.Ordinal)))
            .Message.ShouldContain("created a second time");
    }

    [Fact]
    public void A_Member_Type_Missing_From_The_Options_Names_The_Member()
    {
        var gallery = Gallery.Open("Tate");
        var bare = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() };
        Should.Throw<NotSupportedException>(() => AggregateState.Write(gallery, bare)).Message.ShouldContain("Gallery.Tags");
    }

    [Fact]
    public void A_Broken_Aggregate_Cannot_Be_Written()
    {
        var faulty = Faulty.Create();
        Should.Throw<InvalidOperationException>(() => faulty.Fail());
        Should.Throw<InvalidOperationException>(() => AggregateState.Write(faulty, Options)).Message.ShouldContain("broken");
    }

    // ── [LoadByReplay] ──

    [Fact]
    public void A_Replay_Only_Aggregate_Has_No_State()
    {
        Ledger.StateSchema.ShouldBe(0);
        var ledger = Ledger.Start();
        ledger.Book(5m);
        Should.Throw<InvalidOperationException>(() => AggregateState.Write(ledger, Options)).Message.ShouldContain("[LoadByReplay]");
        Read(ledger.Id, """{"id":"x","version":1,"schema":0}""").IsNone.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => AggregateTest.StoredStateMatchesReplay(ledger, Options));
    }

    // ── the test helper ──

    [Fact]
    public void The_Helper_Catches_State_That_Replay_Does_Not_Rebuild()
    {
        var leaky = Leaky.Create();
        leaky.Bump();
        leaky.Cheat();
        Should.Throw<InvalidOperationException>(() => AggregateTest.StoredStateMatchesReplay(leaky, Options))
            .Message.ShouldContain("\"count\":99");
    }

    [Fact]
    public void The_Helper_Needs_The_Whole_History_After_A_Commit()
    {
        var gallery = Gallery.Open("Tate");
        var history = gallery.UncommittedChanges.ToArray();
        AggregateChanges.Committed(gallery);
        gallery.Tag("x");
        Should.Throw<InvalidOperationException>(() => AggregateTest.StoredStateMatchesReplay(gallery, Options)).Message.ShouldContain("whole history");
        AggregateTest.StoredStateMatchesReplay(gallery, [.. history, .. gallery.UncommittedChanges], Options).Tags.ShouldBe(["x"]);
    }

    [Fact]
    public void The_Buffer_Overload_Writes_The_Same_Bytes()
    {
        var gallery = Gallery.Open("Tate");
        var buffer = new ArrayBufferWriter<byte>();
        AggregateState.Write(buffer, gallery, Options);
        buffer.WrittenSpan.ToArray().ShouldBe(AggregateState.Write(gallery, Options));
    }

    /// <summary>Re-emits JSON with object keys in jsonb's order: shorter keys first, then bytewise.</summary>
    internal static string JsonbOrder(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteSorted(writer, doc.RootElement);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void WriteSorted(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var p in element.EnumerateObject().OrderBy(static p => Encoding.UTF8.GetByteCount(p.Name)).ThenBy(static p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(p.Name);
                        WriteSorted(writer, p.Value);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteSorted(writer, item);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
