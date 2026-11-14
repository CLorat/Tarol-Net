using System.Text.Json;
using System.Text.Json.Serialization;
using Eval.A;
using Tarol.Primitives;
using Tarol.Primitives.ChangeAccessor;
using Tarol.StronglyTypedId;
#if BENCH
if (args.Length > 0 && args[0] == "--bench") { BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly(typeof(Bench).Assembly).Run(args[1..]); return 0; }
#endif
var g = Gallery.Open("Tate"); var art = g.AddRoom("Turner").Hang("Rain"); art.Reprice(1000m);
Console.WriteLine($"equality ignores envelope? {new Artwork.Repriced(5) == new Artwork.Repriced(5)}; raised vs fresh: {(Artwork.Repriced)g.UncommittedChanges[3] == new Artwork.Repriced(1000m)}");
art.RepriceDirect(7); Console.WriteLine($"bypass: live Price={art.Price}, replay Price={Gallery.Rehydrate(g.Id, g.UncommittedChanges.ToArray()).Rooms[0].Artworks[0].Price}");
try { Console.WriteLine("user STJ context: " + (UserJson.Default.GetTypeInfo(typeof(Artwork.Repriced)) is null ? "no JsonTypeInfo for Artwork.Repriced" : JsonSerializer.Serialize(new Artwork.Repriced(5), (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Artwork.Repriced>)UserJson.Default.GetTypeInfo(typeof(Artwork.Repriced))!) + " back=" + (JsonSerializer.Deserialize("{\"Price\":5}", (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Artwork.Repriced>)UserJson.Default.GetTypeInfo(typeof(Artwork.Repriced))!) == new Artwork.Repriced(5)))); } catch (Exception e) { Console.WriteLine("user STJ context: " + e.GetType().Name + ": " + e.Message.Split('.')[0]); }
return 0;
[JsonSerializable(typeof(Artwork.Repriced))] internal sealed partial class UserJson : JsonSerializerContext;
#if BENCH
[BenchmarkDotNet.Attributes.MemoryDiagnoser][BenchmarkDotNet.Attributes.ShortRunJob]
public class Bench
{
    private Gallery _gallery = null!; private Artwork _artwork = null!; private DomainEvent[] _history = null!; private Id<Gallery> _id;
    [BenchmarkDotNet.Attributes.GlobalSetup] public void Setup()
    {
        _gallery = Gallery.Open("g"); _artwork = _gallery.AddRoom("r").Hang("a");
        var g = Gallery.Open("h");
        for (var r = 0; r < 100; r++) { var room = g.AddRoom("r" + r); for (var a = 0; a < 9; a++) room.Hang("a"); }
        var arts = g.Rooms.SelectMany(x => x.Artworks).ToArray();
        for (var i = 0; g.Version < 10_000; i++) arts[i % arts.Length].Reprice(i);
        _history = g.UncommittedChanges.ToArray(); _id = g.Id;
    }
    [BenchmarkDotNet.Attributes.Benchmark(OperationsPerInvoke = 1000)] public void RaiseChildEvent() { for (var i = 0; i < 1000; i++) _artwork.Reprice(i); AggregateRootChangeAccessor.ClearChanges(_gallery); }
    [BenchmarkDotNet.Attributes.Benchmark(OperationsPerInvoke = 1000)] public Gallery RaiseCreate() { var g = Gallery.Open("c"); var room = g.AddRoom("r"); for (var i = 0; i < 998; i++) room.Hang("a"); return g; }
    [BenchmarkDotNet.Attributes.Benchmark] public Gallery Replay10k() => Gallery.Rehydrate(_id, _history);
}
#endif
