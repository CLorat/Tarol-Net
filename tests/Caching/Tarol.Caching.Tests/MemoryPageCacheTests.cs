using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Caching.Tests;

public sealed class MemoryPageCacheTests
{
    private static readonly PageKey Key = new(Lab.TenantA, "auction-list", 1, "s2:id;");

    private static Page<AuctionRow> PageAt(GlobalPosition position) => new([], null, $"W/\"{position.Position:x16}\"", position);

    [Fact]
    public void An_entry_answers_only_for_the_position_it_was_stamped_with()
    {
        using var cache = new MemoryPageCache();
        var at = new GlobalPosition(5, 50);
        cache.Put(Key, at, PageAt(at), 100);

        cache.TryGet(Key, at, out Page<AuctionRow>? hit).ShouldBeTrue();
        hit!.Position.ShouldBe(at);
        cache.TryGet<AuctionRow>(Key, new GlobalPosition(6, 60), out _).ShouldBeFalse();
        cache.TryGet<AuctionRow>(Key with { Tenant = Lab.TenantB }, at, out _).ShouldBeFalse();
        cache.TryGet<string>(Key, at, out _).ShouldBeFalse();            // another row type under the same key is no hit
    }

    [Fact]
    public void Clear_of_a_tenant_drops_only_its_pages()
    {
        // step 16C (DP-5): an erasure drops the tenant's pages, in every process
        using var cache = new MemoryPageCache();
        var at = new GlobalPosition(5, 50);
        cache.Put(Key, at, PageAt(at), 100);
        cache.Put(Key with { Tenant = Lab.TenantB }, at, PageAt(at), 100);

        cache.Clear(Lab.TenantA);

        cache.TryGet<AuctionRow>(Key, at, out _).ShouldBeFalse();
        cache.TryGet<AuctionRow>(Key with { Tenant = Lab.TenantB }, at, out _).ShouldBeTrue();
        (cache.Count, cache.Bytes).ShouldBe((1, 100L));
    }

    [Fact]
    public void A_slower_search_with_an_older_stamp_does_not_replace_a_newer_entry()
    {
        using var cache = new MemoryPageCache();
        var older = new GlobalPosition(5, 50);
        var newer = new GlobalPosition(6, 60);
        cache.Put(Key, newer, PageAt(newer), 100);
        cache.Put(Key, older, PageAt(older), 100);

        cache.TryGet<AuctionRow>(Key, newer, out _).ShouldBeTrue();
        cache.TryGet<AuctionRow>(Key, older, out _).ShouldBeFalse();
        (cache.Count, cache.Bytes).ShouldBe((1, 100L));
    }

    [Fact]
    public void Clear_removes_every_entry()
    {
        using var cache = new MemoryPageCache();
        for (var i = 0; i < 5; i++)
            cache.Put(Key with { Query = $"q{i}" }, default, PageAt(default), 10);

        cache.Clear();

        (cache.Count, cache.Bytes).ShouldBe((0, 0L));
        cache.TryGet<AuctionRow>(Key with { Query = "q1" }, default, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_write_trims_idle_entries_first()
    {
        var time = new FakeTimeProvider(Lab.Now);
        using var cache = new MemoryPageCache(new CacheOptions { MaxEntries = 3, IdleExpiry = TimeSpan.FromMinutes(1) }, time);
        cache.Put(Key with { Query = "old" }, default, PageAt(default), 10);
        time.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 3; i++)
            cache.Put(Key with { Query = $"new{i}" }, default, PageAt(default), 10);

        cache.Count.ShouldBe(3);
        cache.TryGet<AuctionRow>(Key with { Query = "old" }, default, out _).ShouldBeFalse();
        cache.TryGet<AuctionRow>(Key with { Query = "new0" }, default, out _).ShouldBeTrue();
    }

    [Fact]
    public void Bad_bounds_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MemoryPageCache(new CacheOptions { MaxEntries = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new MemoryPageCache(new CacheOptions { MaxBytes = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new MemoryPageCache(new CacheOptions { IdleExpiry = TimeSpan.Zero }));
    }

    [Fact]
    public async Task Concurrent_readers_and_writers_never_see_a_torn_entry_and_the_totals_stay_exact()
    {
        using var cache = new MemoryPageCache(new CacheOptions { MaxEntries = 50, MaxBytes = 40_000 });
        var keys = Enumerable.Range(0, 80).Select(i => Key with { Query = $"q{i}" }).ToArray();
        var torn = 0;
        var hits = 0;
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 20_000; i++)
            {
                var key = keys[random.Next(keys.Length)];
                var at = new GlobalPosition((ulong)random.Next(1, 4), 0);
                if (random.Next(3) == 0)
                {
                    cache.Put(key, at, PageAt(at), random.Next(1, 1_000));
                }
                else if (cache.TryGet(key, at, out Page<AuctionRow>? page))
                {
                    Interlocked.Increment(ref hits);
                    if (page.Position != at || page.ETag != $"W/\"{at.Position:x16}\"")
                        Interlocked.Increment(ref torn);
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(tasks);

        torn.ShouldBe(0);
        hits.ShouldBeGreaterThan(0);
        cache.Count.ShouldBeLessThanOrEqualTo(50 + 8);                    // a writer may pass the cap while another trims
        var count = 0;
        foreach (var key in keys)
            for (var tx = 1UL; tx < 4; tx++)
                if (cache.TryGet<AuctionRow>(key, new GlobalPosition(tx, 0), out _))
                    count++;
        count.ShouldBe(cache.Count);
        cache.Clear();
        (cache.Count, cache.Bytes).ShouldBe((0, 0L));                    // every add and remove was counted once
    }

    [Fact]
    public void The_meter_reports_hits_misses_evictions_entries_and_bytes()
    {
        var time = new FakeTimeProvider(Lab.Now);
        using var meters = new Meters();
        using var cache = new MemoryPageCache(new CacheOptions { IdleExpiry = TimeSpan.FromMinutes(1) }, time, meters);
        var measured = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MemoryPageCache.MeterName && ReferenceEquals(instrument.Meter.Scope, meters))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Add(measured, instrument.Name, value));
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Add(measured, instrument.Name, value));
        listener.Start();

        cache.TryGet<AuctionRow>(Key, default, out _);
        cache.Put(Key, default, PageAt(default), 123);
        cache.TryGet<AuctionRow>(Key, default, out _);
        listener.RecordObservableInstruments();
        time.Advance(TimeSpan.FromMinutes(2));
        cache.TryGet<AuctionRow>(Key, default, out _);

        measured["tarol.caching.hits"].ShouldBe(1);
        measured["tarol.caching.misses"].ShouldBe(2);
        measured["tarol.caching.evictions"].ShouldBe(1);
        measured["tarol.caching.entries"].ShouldBe(1);
        measured["tarol.caching.bytes"].ShouldBe(123);
    }

    private static void Add(Dictionary<string, long> measured, string name, long value)
    {
        lock (measured)
            measured[name] = measured.GetValueOrDefault(name) + value;
    }

    // meters scoped to one test, so parallel tests do not count into each other
    private sealed class Meters : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }
}
