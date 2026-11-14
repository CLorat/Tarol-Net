#if !BENCH
using System.Buffers;
using System.Text;
using System.Text.Json;
using Probe;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

var options = ProbeOptions.Value;
var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  " + detail)}");
    if (!ok)
        failures++;
}

string Write(Gallery g)
{
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
        Gallery.WriteState(writer, g, options);
    return Encoding.UTF8.GetString(buffer.WrittenSpan);
}

Option<Gallery> Read(Id<Gallery> id, string json)
{
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    return Gallery.ReadState(id, ref reader, options);
}

Gallery Some(Option<Gallery> option) => option.GetValueOrThrow();

// ── a history that touches every member kind ──
var at = new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.FromHours(1));
var live = Gallery.Open("Tate", at);
live.Relabel("Modern");
live.Tag("art");
live.Tag("london");
live.SetMotto("Look closer");
live.Move("Millbank", "London");
live.Count(1200);
live.SetBudget(250_000.50m);
var hall = live.AddRoom("Turbine Hall");
var side = live.AddRoom("Side");
hall.Light(Lighting.Dim);
hall.Limit(400);
var w1 = hall.Hang("Weather", 1_000_000m);
var w2 = hall.Hang("Shibboleth", 50_000m);
w1.Reprice(1_100_000m);
w1.Prefer(Lighting.Warm);
w2.SetReserve(40_000m);
w1.Annotate("on loan");
hall.Annotate("busy on Sundays");
side.Close();
live.Appoint("Frances");
_ = live.Summary; // fills the [Transient] cache

var history = live.UncommittedChanges.ToArray();
var stored = Write(live);
Console.WriteLine("state: " + stored);
Console.WriteLine($"bytes: {Encoding.UTF8.GetByteCount(stored)}, events: {history.Length}, schema: {Gallery.StateSchema}");

var replayed = Gallery.Rehydrate(live.Id, history);
Check("state of live == state of full replay", Write(replayed) == stored);

var labelChecks = Label.Checks;
var loaded = Some(Read(live.Id, stored));
Check("read(write(x)) writes the same JSON", Write(loaded) == stored);
Check("restoring runs no rule (value object Validate not called)", Label.Checks == labelChecks);
Check("version restored", loaded.Version == live.Version, $"{loaded.Version}");
Check("no uncommitted changes after a load", loaded.UncommittedChanges.IsEmpty);
Check("invariants pass on a loaded aggregate", AggregateChanges.Prepare(loaded).IsSuccess);
Check("[Transient] cache is not stored, recomputed on demand", !stored.Contains("summary", StringComparison.Ordinal) && loaded.Summary == live.Summary);
Check("registry: TryFind an artwork in the loaded tree", ((IRegistry<Artwork>)loaded).TryFind(w1.Id, out var a1) && a1.Price == 1_100_000m && a1.Notes.Count == 1);
Check("EntityRef restored", loaded.Curator.Value?.Name == "Frances");
Check("multi-owner child restored under both owners", loaded.Rooms[0].Notes.Count == 1 && loaded.Rooms[0].Artworks[0].Notes.Count == 1);

// ── jsonb reorders keys (shorter first, then bytewise): the reader must not depend on order ──
var reordered = JsonbOrder(stored);
Check("jsonb key order reads back", Write(Some(Read(live.Id, reordered))) == stored);

// ── behaviour after a load: the same commands on live and loaded give the same events and state ──
AggregateChanges.Committed(live);
var cmds = new Action<Gallery>[]
{
    g => g.Rooms[0].Artworks[1].Reprice(60_000m),
    g => g.Rooms[0].Hang("Fountain", 3_000m, Id<Artwork>.From(Guid.Parse("01900000-0000-7000-8000-000000000001"))),
    g => g.Rooms[0].Close(),
    g => g.Curator.Value!.Resign(),
    g => g.AddRoom("New wing", Id<Room>.From(Guid.Parse("01900000-0000-7000-8000-000000000002"))),
};
foreach (var cmd in cmds)
{
    cmd(live);
    cmd(loaded);
}

Check("same versions after continuing on the loaded instance", loaded.Version == live.Version && loaded.UncommittedChanges[0].Version == history.Length + 1);
Check("same state after continuing (cascade, ref delete, create)", Write(loaded) == Write(live));
Check("cascade on loaded: the closed room's artworks left the registry", !((IRegistry<Artwork>)loaded).TryFind(w1.Id, out _));
var all = history.Concat(live.UncommittedChanges.ToArray()).ToArray();
Check("state after continuing == full replay", Write(Gallery.Rehydrate(live.Id, all)) == Write(loaded));

// ── shape changes: another schema reads as None and the reader moves past the object ──
var otherSchema = stored.Replace($"\"schema\":{Gallery.StateSchema}", "\"schema\":12345", StringComparison.Ordinal);
var bytes = Encoding.UTF8.GetBytes(otherSchema);
var r = new Utf8JsonReader(bytes);
var none = Gallery.ReadState(live.Id, ref r, options);
Check("another schema -> None", none.IsNone);
Check("... and the object was skipped", r.BytesConsumed == bytes.Length);
Check("no schema member -> None", Read(live.Id, stored.Replace($",\"schema\":{Gallery.StateSchema}", "", StringComparison.Ordinal)).IsNone);

Throws<InvalidOperationException>("foreign id throws", () => Read(Id<Gallery>.New(), stored));
Throws<JsonException>("unknown member throws", () => Read(live.Id, stored.Replace("\"name\":", "\"title\":", StringComparison.Ordinal)));
Throws<JsonException>("missing member throws", () => Read(live.Id, stored.Replace(",\"motto\":\"Look closer\"", "", StringComparison.Ordinal)));
Throws<JsonException>("unknown enum name throws", () => Read(live.Id, stored.Replace("\"Dim\"", "\"Bright\"", StringComparison.Ordinal)));
Throws<InvalidOperationException>("duplicate entity id throws", () =>
{
    var room = JsonDocument.Parse(stored).RootElement.GetProperty("rooms")[0].GetRawText();
    return Read(live.Id, stored.Replace("\"rooms\":[" + room, "\"rooms\":[" + room + "," + room, StringComparison.Ordinal));
});

// ── empty, None and null members ──
var bare = Gallery.Open("Bare", at, Id<Gallery>.From(Guid.Parse("01900000-0000-7000-8000-0000000000aa")));
var bareJson = Write(bare);
Console.WriteLine("bare: " + bareJson);
Check("bare aggregate round-trips", Write(Some(Read(bare.Id, bareJson))) == bareJson);

// ── [LoadByReplay] ──
Check("[LoadByReplay]: StateSchema is 0", Ledger.StateSchema == 0);
Throws<InvalidOperationException>("[LoadByReplay]: WriteState throws", () =>
{
    using var w = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
    Ledger.WriteState(w, Ledger.Start(), options);
    return 0;
});
var lr = new Utf8JsonReader("{\"id\":\"x\"}"u8);
Check("[LoadByReplay]: ReadState is None", Ledger.ReadState(Id<Ledger>.New(), ref lr, options).IsNone);

// ── JsonMetadataServices variant (write side only) produces the same JSON ──
Check("JsonMetadataServices writer == generated writer", MetadataVariant.Write(live, options) == Write(live));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

void Throws<T>(string name, Func<object?> action) where T : Exception
{
    try
    {
        action();
        Check(name, false, "no exception");
    }
    catch (T e)
    {
        Check(name, true, e.GetType().Name + ": " + e.Message);
    }
}

static string JsonbOrder(string json)
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
#endif
