using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Probe;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

BenchmarkRunner.Run<StoredState>(args: args);

/// <summary>
/// A Gallery with N entities (rooms = max(1, N/10), the rest artworks; every artwork hung and repriced once): the cost of
/// writing its state on a commit, reading it on a load, and the replay it replaces (in memory, and with event JSON).
/// </summary>
[MemoryDiagnoser]
public class StoredState
{
    private readonly ArrayBufferWriter<byte> _buffer = new(64 * 1024);
    private readonly JsonSerializerOptions _options = ProbeOptions.Value;
    private Utf8JsonWriter _writer = null!;
    private Gallery _gallery = null!;
    private StreamEvent[] _history = null!;
    private (StreamEvent Envelope, byte[] Json)[] _stored = null!;
    private byte[] _state = null!;

    [Params(1, 10, 100)]
    public int Entities { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var g = Gallery.Open("Bench", DateTimeOffset.UnixEpoch);
        var rooms = Math.Max(1, Entities / 10);
        var list = new List<Room>();
        for (var i = 0; i < rooms; i++)
            list.Add(g.AddRoom("Room " + i));
        for (var i = 0; i < Entities - rooms; i++)
            list[i % rooms].Hang("Artwork " + i, 100 + i).Reprice(200 + i);
        _gallery = g;
        _history = g.UncommittedChanges.ToArray();
        _stored = [.. _history.Select(e => (e, Serialize(e.Event)))];
        _writer = new Utf8JsonWriter(_buffer);
        Write();
        _state = _buffer.WrittenSpan.ToArray();
        Console.WriteLine($"// N={Entities}: {_history.Length} events, state {_state.Length} B, event JSON {_stored.Sum(static s => s.Json.Length)} B");
        if (MetadataWrite() != _state.Length || ReadState() != _gallery.Version || Replay() != _gallery.Version || ReplayFromJson() != _gallery.Version)
            throw new InvalidOperationException("variants disagree");
    }

    [Benchmark]
    public int Write()
    {
        _buffer.ResetWrittenCount();
        _writer.Reset(_buffer);
        Gallery.WriteState(_writer, _gallery, _options);
        _writer.Flush();
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public int MetadataWrite()
    {
        _buffer.ResetWrittenCount();
        _writer.Reset(_buffer);
        MetadataVariant.Write(_writer, _gallery, _options);
        _writer.Flush();
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public long ReadState()
    {
        var reader = new Utf8JsonReader(_state);
        return Gallery.ReadState(_gallery.Id, ref reader, _options).GetValueOrThrow().Version;
    }

    [Benchmark(Baseline = true)]
    public long Replay() => Gallery.Rehydrate(_gallery.Id, _history).Version;

    /// <summary>What a load by replay costs a store: deserialize every event payload, then replay.</summary>
    [Benchmark]
    public long ReplayFromJson()
    {
        var events = new StreamEvent[_stored.Length];
        for (var i = 0; i < events.Length; i++)
        {
            var (e, json) = _stored[i];
            events[i] = e with { Event = Deserialize(e.Event, json) };
        }

        return Gallery.Rehydrate(_gallery.Id, events).Version;
    }

    private byte[] Serialize(DomainEvent e) => e switch
    {
        Gallery.Opened x => JsonSerializer.SerializeToUtf8Bytes(x, ProbeJson.Default.Opened),
        Room.Added x => JsonSerializer.SerializeToUtf8Bytes(x, ProbeJson.Default.Added),
        Artwork.Hung x => JsonSerializer.SerializeToUtf8Bytes(x, ProbeJson.Default.Hung),
        Artwork.Repriced x => JsonSerializer.SerializeToUtf8Bytes(x, ProbeJson.Default.Repriced),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    // the store picks the type by alias; here by the original instance's type
    private static DomainEvent Deserialize(DomainEvent original, byte[] json) => original switch
    {
        Gallery.Opened => JsonSerializer.Deserialize(json, ProbeJson.Default.Opened)!,
        Room.Added => JsonSerializer.Deserialize(json, ProbeJson.Default.Added)!,
        Artwork.Hung => JsonSerializer.Deserialize(json, ProbeJson.Default.Hung)!,
        Artwork.Repriced => JsonSerializer.Deserialize(json, ProbeJson.Default.Repriced)!,
        _ => throw new NotSupportedException(original.GetType().Name),
    };
}
