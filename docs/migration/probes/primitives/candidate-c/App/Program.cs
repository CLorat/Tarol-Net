using System.Text.Json;
using System.Text.Json.Serialization;
using C.Core;
using C.Domain;
using Tarol.Results;
using Tarol.StronglyTypedId;

#if BENCH
if (args.Length > 0 && args[0] == "--bench")
{
    BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly(typeof(Bench).Assembly).Run(args[1..]);
    return 0;
}
#endif

var failures = 0;
void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}"); if (!ok) failures++; }
static bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }

var gallery = Gallery.Open("Tate");
var room = gallery.AddRoom("Turner");
var art = room.Hang("Rain");
art.Reprice(1000m);
Check("guard returns Error, no event", gallery.Tag("x").IsSuccess && !gallery.Tag("x").IsSuccess && gallery.Version == 5);
var second = gallery.AddRoom("Closed");
second.Hang("Gone");
second.Close();
var history = gallery.UncommittedChanges.ToArray();
var replayed = Gallery.Rehydrate(gallery.Id, history);
Check("replay = live (ancestor handler, cascade)", replayed.Rooms.Single().TotalValue == 1000m && replayed.Version == gallery.Version
    && ((IRegistry<Artwork>)replayed).Find(art.Id) is not null && replayed.Rooms.Count == 1);
Check("events are pure payload: value equality", new Artwork.Repriced(5) == new Artwork.Repriced(5));
Check("envelope beside the event", history[3] is { Version: 4, Event: Artwork.Repriced { Price: 1000m } } && history[3].EntityId == art.Id.Value);

// Same-assembly STJ context lists the user's events (real types, no SYSLIB1030).
var json = JsonSerializer.Serialize(new Artwork.Hung("Rain"), AppJson.Default.Hung);
Check($"same-assembly STJ context: {json}", json == "{\"Name\":\"Rain\"}" && JsonSerializer.Deserialize(json, AppJson.Default.Hung) == new Artwork.Hung("Rain"));

// Invariants on the hand-off: a negative price is a Conflict, events stay uncommitted.
Check("invariants pass", AggregateChanges.Prepare(gallery).IsSuccess);
AggregateChanges.Committed(gallery);
art.Reprice(-1);
var prepared = AggregateChanges.Prepare(gallery);
Check($"invariant violation on hand-off: {prepared}", !prepared.IsSuccess && gallery.UncommittedChanges.Length == 1);
Check("replay of the violating history still loads (no rule on replay)",
    Gallery.Rehydrate(gallery.Id, [.. history, gallery.UncommittedChanges[0] with { Version = history.Length + 1 }]).Rooms[0].Artworks[0].Price == -1);

// Strict replay.
Check("unknown entity update throws", Throws(() => Gallery.Rehydrate(gallery.Id, [history[0], history[3] with { Version = 2 }])));
Check("gap throws", Throws(() => Gallery.Rehydrate(gallery.Id, [history[0], history[2]])));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

[JsonSerializable(typeof(Artwork.Hung))]
[JsonSerializable(typeof(Artwork.Repriced))]
[JsonSerializable(typeof(Gallery.Opened))]
internal sealed partial class AppJson : JsonSerializerContext;

#if BENCH
[BenchmarkDotNet.Attributes.MemoryDiagnoser]
[BenchmarkDotNet.Attributes.ShortRunJob]
public class Bench
{
    private Gallery _gallery = null!;
    private Artwork _artwork = null!;
    private StreamEvent[] _history = null!;

    [BenchmarkDotNet.Attributes.GlobalSetup]
    public void Setup()
    {
        _gallery = Gallery.Open("g");
        _artwork = _gallery.AddRoom("r").Hang("a");
        var g = Gallery.Open("h");
        for (var r = 0; r < 100; r++)
        {
            var room = g.AddRoom("r" + r);
            for (var a = 0; a < 9; a++) room.Hang("a");
        }
        var arts = g.Rooms.SelectMany(x => x.Artworks).ToArray();
        for (var i = 0; g.Version < 10_000; i++) arts[i % arts.Length].Reprice(i);
        _history = g.UncommittedChanges.ToArray();
    }

    [BenchmarkDotNet.Attributes.Benchmark(OperationsPerInvoke = 1000)]
    public void RaiseChildEvent()
    {
        for (var i = 0; i < 1000; i++) _artwork.Reprice(i);
        AggregateChanges.Committed(_gallery);
    }

    [BenchmarkDotNet.Attributes.Benchmark(OperationsPerInvoke = 1000)]
    public Gallery RaiseCreate()
    {
        var g = Gallery.Open("c");
        var room = g.AddRoom("r");
        for (var i = 0; i < 998; i++) room.Hang("a");
        return g;
    }

    [BenchmarkDotNet.Attributes.Benchmark]
    public Gallery Replay10k() => Gallery.Rehydrate(_history[0].EntityId is var id ? (Id<Gallery>)id : default, _history);
}
#endif
