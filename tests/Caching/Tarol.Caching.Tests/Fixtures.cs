using System.Text.Json.Serialization;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;

namespace Tarol.Caching.Tests;

public enum AuctionStatus
{
    Open,
    Closed,
}

/// <summary>An aggregate the row is projected from (only its events are needed: the tests write batches directly).</summary>
public partial class Auction : AggregateRoot<Auction>
{
    public sealed record Opened(string Seller, string Title, DateTimeOffset EndsAt) : Event;

    public sealed record BidPlaced(string Bidder, decimal Amount) : Event;

#pragma warning disable CA1822 // the row is written by the tests; the aggregate only declares the events
    private void On(Opened e) { }

    private void On(BidPlaced e) { }
#pragma warning restore CA1822
}

[Projection("auction-list", Version = 1)]
public sealed partial record AuctionRow(Guid Id, string Title, string Seller, [Sort] DateTimeOffset EndsAt, AuctionStatus Status, [Sort] decimal HighestBid)
{
    public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.Seller, e.EndsAt, AuctionStatus.Open, 0m);

    public AuctionRow When(Auction.BidPlaced e) => this with { HighestBid = e.Amount };
}

[JsonSerializable(typeof(AuctionRow))]
public sealed partial class CachingJson : JsonSerializerContext;

/// <summary>The read stores, a counting decorator under the cache, and a writer that moves the checkpoint batch by batch.</summary>
internal sealed class Lab
{
    public static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    public static readonly Guid TenantA = Guid.Parse("0196f000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0196f000-0000-7000-8000-00000000000b");

    private readonly Dictionary<RowKey, (long Version, AuctionRow Row)> _rows = [];
    private GlobalPosition _checkpoint;
    private long _tx = 100;

    public Lab(CacheOptions? options = null, TimeProvider? time = null, bool current = true)
    {
        Time = time ?? TimeProvider.System;
        Counting = new CountingReadModels(InMemory);
        Cache = new MemoryPageCache(options, Time);
        Cached = new CachedReadModels(Counting, new ProjectionPositions(InMemory), Cache, CachingJson.Default.Options);
        if (current)
            Batch();                                                       // an empty batch: the projection is current
    }

    public TimeProvider Time { get; }

    public InMemoryReadModels InMemory { get; } = new();

    public CountingReadModels Counting { get; }

    public MemoryPageCache Cache { get; }

    public CachedReadModels Cached { get; }

    public IReadModelStore<AuctionRow> Store => Cached.Store<AuctionRow>();

    /// <summary>Searches of the inner store (misses).</summary>
    public int Searches => Counting.Searches;

    public GlobalPosition Checkpoint => _checkpoint;

    /// <summary>Writes one batch with the given rows (open auctions by default) and moves the checkpoint.</summary>
    public GlobalPosition Batch(Guid tenant = default, params (string Title, string Seller)[] opened)
    {
        var rows = new List<Versioned<AuctionRow>>();
        foreach (var (title, seller) in opened)
        {
            var id = Guid.CreateVersion7(Now.AddSeconds(_rows.Count));
            var row = new AuctionRow(id, title, seller, Now.AddHours(_rows.Count + 1), AuctionStatus.Open, 0m);
            var key = new RowKey(tenant, id);
            _rows[key] = (1, row);
            rows.Add(new Versioned<AuctionRow>(key, 1, row));
        }
        return Write(rows);
    }

    /// <summary>A bid on an existing row.</summary>
    public GlobalPosition Bid(Guid tenant, Guid id, decimal amount)
    {
        var key = new RowKey(tenant, id);
        var (version, row) = _rows[key];
        var next = row with { HighestBid = amount };
        _rows[key] = (version + 1, next);
        return Write([new Versioned<AuctionRow>(key, version + 1, next)]);
    }

    private GlobalPosition Write(IReadOnlyList<Versioned<AuctionRow>> rows)
    {
        var to = new GlobalPosition((ulong)++_tx, _tx * 10);
        InMemory.Store<AuctionRow>().Write(new ProjectionBatch<AuctionRow>(rows, _checkpoint, to, ProjectionState.Current), default)
            .AsTask().GetAwaiter().GetResult().ShouldBeTrue();
        _checkpoint = to;
        return to;
    }

    public async Task<Page<AuctionRow>> Page(ListQuery query)
    {
        var result = await Store.Search(query, TestContext.Current.CancellationToken);
        result.TryGetValue(out Page<AuctionRow>? page).ShouldBeTrue(result.ToString());
        return page!;
    }
}

/// <summary>Counts every call that reaches the inner store; <see cref="Gate"/> holds a search until it is released.</summary>
internal sealed class CountingReadModels(IReadModels inner) : IReadModels
{
    private readonly Dictionary<Type, object> _stores = [];
    private int _searches;

    public int Searches => Volatile.Read(ref _searches);

    public Dictionary<string, int> Calls { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource? Entered { get; set; }

    /// <summary>Runs after the inner search read its rows, before the page is cached (where an erasure can come in).</summary>
    public Func<Task>? AfterSearch { get; set; }

    public IReadModelStore<TRow> Store<TRow>()
        where TRow : class, IProjection<TRow>
    {
        lock (_stores)
        {
            if (!_stores.TryGetValue(typeof(TRow), out var store))
                _stores[typeof(TRow)] = store = new Counting<TRow>(this, inner.Store<TRow>());
            return (IReadModelStore<TRow>)store;
        }
    }

    private void Count(string member)
    {
        lock (Calls)
            Calls[member] = Calls.GetValueOrDefault(member) + 1;
    }

    private sealed class Counting<TRow>(CountingReadModels owner, IReadModelStore<TRow> inner) : IReadModelStore<TRow>
        where TRow : class, IProjection<TRow>
    {
        public ValueTask Replace(IReadOnlyList<Versioned<TRow>> rows, CancellationToken cancellationToken) => inner.Replace(rows, cancellationToken);

        public ValueTask<int> Remove(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken) => inner.Remove(keys, cancellationToken);

        public ValueTask Prepare(CancellationToken cancellationToken)
        {
            owner.Count(nameof(Prepare));
            return inner.Prepare(cancellationToken);
        }

        public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken)
        {
            owner.Count(nameof(Checkpoint));
            return inner.Checkpoint(cancellationToken);
        }

        public ValueTask<IReadOnlyDictionary<RowKey, Versioned<TRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
        {
            owner.Count(nameof(Load));
            return inner.Load(keys, cancellationToken);
        }

        public ValueTask<bool> Write(ProjectionBatch<TRow> batch, CancellationToken cancellationToken)
        {
            owner.Count(nameof(Write));
            return inner.Write(batch, cancellationToken);
        }

        public ValueTask<Versioned<TRow>?> Find(RowKey key, CancellationToken cancellationToken)
        {
            owner.Count(nameof(Find));
            return inner.Find(key, cancellationToken);
        }

        public async ValueTask<Result<Page<TRow>>> Search(ListQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._searches);
            owner.Entered?.TrySetResult();
            if (owner.Gate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            var result = await inner.Search(query, cancellationToken);
            if (owner.AfterSearch is { } after)
            {
                owner.AfterSearch = null;
                await after();
            }
            return result;
        }
    }
}
