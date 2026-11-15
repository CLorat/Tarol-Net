using ArtFlow;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.StronglyTypedId;
using Tarol.Validations;

BenchmarkSwitcher.FromAssembly(typeof(StoreBench).Assembly).Run(args);

[MemoryDiagnoser]
public class StoreBench
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("PROBE_PG") ?? "Host=localhost;Username=postgres;Password=probe";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private NpgsqlDataSource _db = null!;
    private NpgsqlAggregateStore _pgState = null!, _pgReplay = null!;
    private InMemoryAggregateStore _memState = null!, _memReplay = null!;
    private OperationContext _ctx = null!;
    private Id<Auction> _pgStream, _memStream, _pgWrite, _memWrite;

    [Params(10, 100)] public int Events { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var json = ValueObjectJson.Options(AppJson.Default);
        json.MakeReadOnly();
        _db = new NpgsqlSlimDataSourceBuilder(Cs).EnableArrays().Build();
        await using (var c = _db.CreateCommand("DROP SCHEMA IF EXISTS b_bench CASCADE")) await c.ExecuteNonQueryAsync();
        var codec = new EventCodec(json).Add<Auction>(Catalogs.Auction).Add<Ledger>(Catalogs.Ledger);
        var lookups = new LookupDefinition[] { new KeyLookup("auction", "lot", a => ((Auction)a).Lot) };
        _pgState = new(_db, codec, _time, new() { Schema = "b_bench", StateOptions = json, Lookups = lookups });
        _pgReplay = new(_db, codec, _time, new() { Schema = "b_bench", Lookups = lookups });
        await _pgState.EnsureSchema(default);
        _memState = new InMemoryAggregateStore(_time, json).DefineLookup<Auction>("lot", a => a.Lot);
        _memReplay = new InMemoryAggregateStore(_time).DefineLookup<Auction>("lot", a => a.Lot);
        _ctx = OperationContext.Start(Actor.User("bench"), Option.None, _time);
        _pgStream = await Seed(_pgState, Events);
        _memStream = await Seed(_memState, Events);
        await Seed(_memReplay, Events, _memStream);
        _pgWrite = await Seed(_pgState, 1);
        _memWrite = await Seed(_memState, 1);
    }

    private async Task<Id<Auction>> Seed(IAggregateStore store, int events, Id<Auction>? id = null)
    {
        var a = Auction.Open(id ?? Id<Auction>.New(_time), "s", Title(), $"LOT-{Guid.NewGuid()}", 1);
        for (var i = 1; i < events; i++) _ = a.PlaceBid($"b{i}", 1 + i);
        var r = await store.Append(a, 0, _ctx, Option.None, default);
        if (!r.IsSuccess) throw new InvalidOperationException(r.ToString());
        return a.Id;
    }

#pragma warning disable TVAL006
    private static Title Title() => new("Sunflowers");
#pragma warning restore TVAL006

    [Benchmark] public async Task<Auction> Load_InMemory_State() => (await _memState.Load(_memStream, _ctx, default)).TryGetValue(out Auction? a) ? a : throw new InvalidOperationException();
    [Benchmark] public async Task<Auction> Load_InMemory_Replay() => (await _memReplay.Load(_memStream, _ctx, default)).TryGetValue(out Auction? a) ? a : throw new InvalidOperationException();
    [Benchmark] public async Task<Auction> Load_Npgsql_State() => (await _pgState.Load(_pgStream, _ctx, default)).TryGetValue(out Auction? a) ? a : throw new InvalidOperationException();
    [Benchmark] public async Task<Auction> Load_Npgsql_Replay() => (await _pgReplay.Load(_pgStream, _ctx, default)).TryGetValue(out Auction? a) ? a : throw new InvalidOperationException();

    // load + one event + append (the change pipeline's store work), on a short stream that grows
    [Benchmark] public Task<bool> Change_InMemory() => Change(_memState, _memWrite);
    [Benchmark] public Task<bool> Change_Npgsql() => Change(_pgState, _pgWrite);

    private async Task<bool> Change(IAggregateStore store, Id<Auction> id)
    {
        var a = (await store.Load(id, _ctx, default)).TryGetValue(out Auction? x) ? x : throw new InvalidOperationException();
        var expected = a.Version;
        _ = a.Relot($"L-{expected}");
        var r = await store.Append(a, expected, _ctx, Option.None, default);
        return r.IsSuccess ? true : throw new InvalidOperationException(r.ToString());
    }
}
