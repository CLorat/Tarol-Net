using System.Collections.Concurrent;
using Tarol.Results;

namespace Tarol.Queries;

/// <summary>
/// The read store of one projection version in memory (tests, prototypes): rows and checkpoint under one lock, the guarded
/// write, and the list query semantics of the Npgsql store (ordinal text, uuids by their bytes, the id tie-break).
/// Thread-safe.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
public sealed class InMemoryReadModelStore<TRow> : IReadModelStore<TRow>
    where TRow : class, IProjection<TRow>
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RowKey, Versioned<TRow>> _rows = [];
    private ProjectionCheckpoint _checkpoint;
    private int _refused;

    /// <summary>Every stored row (all tenants), in no particular order.</summary>
    public IReadOnlyList<Versioned<TRow>> Rows
    {
        get
        {
            lock (_gate)
                return [.. _rows.Values];
        }
    }

    /// <summary>How many writes the checkpoint guard refused (tests of two runners).</summary>
    public int Refused => Volatile.Read(ref _refused);

    /// <inheritdoc />
    public ValueTask Prepare(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_checkpoint);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<RowKey, Versioned<TRow>>> Load(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var found = new Dictionary<RowKey, Versioned<TRow>>(keys.Count);
        lock (_gate)
        {
            foreach (var key in keys)
                if (_rows.TryGetValue(key, out var row))
                    found[key] = row;
        }
        return ValueTask.FromResult<IReadOnlyDictionary<RowKey, Versioned<TRow>>>(found);
    }

    /// <inheritdoc />
    public ValueTask<bool> Write(ProjectionBatch<TRow> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_checkpoint.Position != batch.From)
            {
                _refused++;
                return ValueTask.FromResult(false);
            }
            foreach (var row in batch.Rows)
                if (!_rows.TryGetValue(row.Key, out var old) || old.Version < row.Version)
                    _rows[row.Key] = row;
            _checkpoint = new ProjectionCheckpoint(batch.To, batch.State);
        }
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask Replace(IReadOnlyList<Versioned<TRow>> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            foreach (var row in rows)
                _rows[row.Key] = row;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> Remove(IReadOnlyCollection<RowKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        var removed = 0;
        lock (_gate)
            foreach (var key in keys)
                removed += _rows.Remove(key) ? 1 : 0;
        return ValueTask.FromResult(removed);
    }

    /// <inheritdoc />
    public ValueTask<int> RemoveStreams(IReadOnlyCollection<RowKey> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        cancellationToken.ThrowIfCancellationRequested();
        var wanted = streams.ToHashSet();
        var removed = 0;
        lock (_gate)
            foreach (var key in _rows.Values.Where(r => wanted.Contains(new RowKey(r.Key.Tenant, r.Stream))).Select(static r => r.Key).ToList())
                removed += _rows.Remove(key) ? 1 : 0;
        return ValueTask.FromResult(removed);
    }

    /// <inheritdoc />
    public ValueTask<Versioned<TRow>?> Find(RowKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_rows.TryGetValue(key, out var row) ? row : null);
    }

    /// <inheritdoc />
    public ValueTask<Result<Page<TRow>>> Search(ListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var planned = ListQueryPlan<TRow>.Create(query);
        if (!planned.TryGetValue(out ListQueryPlan<TRow>? plan))
            return ValueTask.FromResult<Result<Page<TRow>>>(planned.TryGetValue(out Violation? invalid) ? invalid : Error.Internal("Query.Plan"));
        List<Versioned<TRow>> matching;
        ProjectionCheckpoint checkpoint;
        lock (_gate)
        {
            checkpoint = _checkpoint;
            matching = [.. _rows.Values.Where(r => r.Key.Tenant == query.Tenant && plan.Matches(r))];
        }
        var sign = query.Descending ? -1 : 1;
        matching.Sort((a, b) => sign * Compare(plan.Sort, a, b));
        IEnumerable<Versioned<TRow>> rows = matching;
        if (plan.HasCursor)
            rows = rows.Where(r => sign * CompareToCursor(plan, r) > 0);
        else if (plan.Offset > 0)
            rows = rows.Skip(plan.Offset);
        var page = plan.ToPage([.. rows.Take(plan.Limit + 1)], checkpoint.Position, query.Count ? matching.Count : null);
        return ValueTask.FromResult<Result<Page<TRow>>>(page);
    }

    private static int Compare(ProjectionColumn<TRow>? sort, Versioned<TRow> a, Versioned<TRow> b)
    {
        var bySort = sort is null ? 0 : sort.CompareTo(a.Row, sort.Value(b.Row));
        return bySort != 0 ? bySort : ColumnValues.CompareIds(a.Key.Id, b.Key.Id);
    }

    private static int CompareToCursor(ListQueryPlan<TRow> plan, Versioned<TRow> row)
    {
        var bySort = plan.Sort is null ? 0 : plan.Sort.CompareTo(row.Row, plan.CursorValue);
        return bySort != 0 ? bySort : ColumnValues.CompareIds(row.Key.Id, plan.CursorId);
    }
}

/// <summary>The in-memory read stores of an application: one <see cref="InMemoryReadModelStore{TRow}"/> per row type.</summary>
public sealed class InMemoryReadModels : IReadModels
{
    private readonly ConcurrentDictionary<Type, object> _stores = new();

    /// <inheritdoc />
    IReadModelStore<TRow> IReadModels.Store<TRow>() => Store<TRow>();

    /// <summary>The typed in-memory store of <typeparamref name="TRow"/> (inspect its rows in tests).</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <returns>The store.</returns>
    public InMemoryReadModelStore<TRow> Store<TRow>()
        where TRow : class, IProjection<TRow>
        => (InMemoryReadModelStore<TRow>)_stores.GetOrAdd(typeof(TRow), static _ => new InMemoryReadModelStore<TRow>());
}
