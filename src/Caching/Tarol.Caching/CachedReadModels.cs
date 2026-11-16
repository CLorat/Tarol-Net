using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tarol.Queries;
using Tarol.Results;

namespace Tarol.Caching;

/// <summary>
/// A decorator of <see cref="IReadModels"/> (CA-1, CA-5): per row type, a store whose <see cref="IReadModelStore{TRow}.Search"/>
/// answers from the <see cref="IPageCache"/>. It reads <see cref="IProjectionPositions.Checkpoint{TRow}"/> first (from memory
/// with pushed checkpoints), then asks the cache for that position, and on a miss searches the inner store and caches the
/// page stamped with that position. <c>Prepare</c>, <c>Checkpoint</c>, <c>Load</c>, <c>Write</c> and <c>Find</c> pass
/// through: the worker's writes and every consistent read are untouched. Nothing is cached while the projection builds,
/// and a query the store refuses (<see cref="Invalid"/>) is not cached.
/// </summary>
public sealed class CachedReadModels : IReadModels
{
    private readonly Func<IProjectionPositions> _resolve;
    private readonly JsonSerializerOptions? _json;
    private readonly ConcurrentDictionary<Type, object> _stores = new();
    private IProjectionPositions? _positions;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The read stores (Npgsql or in memory).</param>
    /// <param name="positions">Where the projections stand (<c>NpgsqlProjectionPositions</c> with pushed checkpoints).</param>
    /// <param name="cache">The page cache.</param>
    /// <param name="json">The application's JSON options, to estimate a page's size; <see langword="null"/>: 256 bytes per row.</param>
    public CachedReadModels(IReadModels inner, IProjectionPositions positions, IPageCache cache, JsonSerializerOptions? json = null)
        : this(inner, Resolved(positions), cache, json)
    {
    }

    internal CachedReadModels(IReadModels inner, Func<IProjectionPositions> positions, IPageCache cache, JsonSerializerOptions? json)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        Inner = inner;
        _resolve = positions;
        Cache = cache;
        _json = json;
    }

    /// <summary>The decorated read stores.</summary>
    public IReadModels Inner { get; }

    /// <summary>The page cache.</summary>
    public IPageCache Cache { get; }

    /// <inheritdoc />
    public IReadModelStore<TRow> Store<TRow>()
        where TRow : class, IProjection<TRow>
        => (IReadModelStore<TRow>)_stores.GetOrAdd(typeof(TRow), static (_, models) => new CachedStore<TRow>(models, models.Inner.Store<TRow>()), this);

    // the positions resolve on first use: in DI they depend on IReadModels, which is this decorator
    private IProjectionPositions Positions => _positions ??= _resolve();

    private static Func<IProjectionPositions> Resolved(IProjectionPositions positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return () => positions;
    }

    private sealed class CachedStore<TRow>(CachedReadModels models, IReadModelStore<TRow> inner) : IReadModelStore<TRow>
        where TRow : class, IProjection<TRow>
    {
        private readonly JsonTypeInfo<TRow>? _json = TypeInfo(models._json);

        public ValueTask Prepare(CancellationToken cancellationToken) => inner.Prepare(cancellationToken);

        public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => inner.Checkpoint(cancellationToken);

        public ValueTask<IReadOnlyDictionary<RowKey, Versioned<TRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
            => inner.Load(keys, cancellationToken);

        public ValueTask<bool> Write(ProjectionBatch<TRow> batch, CancellationToken cancellationToken) => inner.Write(batch, cancellationToken);

        public ValueTask<Versioned<TRow>?> Find(RowKey key, CancellationToken cancellationToken) => inner.Find(key, cancellationToken);

        public async ValueTask<Result<Page<TRow>>> Search(ListQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var planned = ListQueryPlan<TRow>.Create(query);
            if (!planned.TryGetValue(out ListQueryPlan<TRow>? plan))
                return await inner.Search(query, cancellationToken).ConfigureAwait(false);       // the store answers Invalid itself
            var key = PageKey.Of(plan);
            // the stamp is read BEFORE the search: a batch that lands during it makes the next request miss
            var checkpoint = await models.Positions.Checkpoint<TRow>(cancellationToken).ConfigureAwait(false);
            if (checkpoint.State == ProjectionState.Current && models.Cache.TryGet(key, checkpoint.Position, out Page<TRow>? hit))
                return hit;
            var result = await inner.Search(query, cancellationToken).ConfigureAwait(false);
            if (checkpoint.State == ProjectionState.Current && result.TryGetValue(out Page<TRow>? page))
                models.Cache.Put(key, checkpoint.Position, page, Size(key, page));
            return result;
        }

        private long Size(PageKey key, Page<TRow> page)
        {
            long size = 64 + (2L * (key.Query.Length + key.Projection.Length + page.ETag.Length + (page.Next?.Length ?? 0)));
            if (_json is null)
                return size + (256L * page.Items.Count);
            var counter = ByteCounter.Shared;
            counter.Reset();
            using (var writer = new Utf8JsonWriter(counter, new JsonWriterOptions { SkipValidation = true }))
                foreach (var row in page.Items)
                    JsonSerializer.Serialize(writer, row, _json);
            return size + counter.Written;
        }

        private static JsonTypeInfo<TRow>? TypeInfo(JsonSerializerOptions? json)
        {
            if (json is null)
                return null;
            try
            {
                return json.GetTypeInfo<TRow>();
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
            {
                return null;                                                 // not in the context: the estimate per row
            }
        }
    }

    // counts the bytes a writer produces into one reused buffer per thread
    private sealed class ByteCounter : IBufferWriter<byte>
    {
        [ThreadStatic]
        private static ByteCounter? _shared;

        private byte[] _buffer = new byte[4096];

        public static ByteCounter Shared => _shared ??= new ByteCounter();

        public long Written { get; private set; }

        public void Reset() => Written = 0;

        public void Advance(int count) => Written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Buffer(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Buffer(sizeHint);

        private byte[] Buffer(int sizeHint)
        {
            if (sizeHint > _buffer.Length)
                _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
            return _buffer;
        }
    }
}
