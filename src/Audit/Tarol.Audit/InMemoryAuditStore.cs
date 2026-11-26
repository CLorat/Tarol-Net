using Tarol.Queries;

namespace Tarol.Audit;

/// <summary>The change log in memory (tests, single-process apps): the same semantics as the PostgreSQL store.</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ProjectionCheckpoint> _checkpoints = [];
    private readonly Dictionary<int, Dictionary<Guid, AuditEntry>> _rows = [];
    private readonly Dictionary<int, Dictionary<AuditValueKey, string?>> _values = [];
    private readonly List<AuditEntry> _access = [];

    /// <inheritdoc />
    public ValueTask Prepare(int version, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rows.TryAdd(version, []);
            _values.TryAdd(version, []);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint(int version, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault(version));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<AuditValueKey, string?>> Values(int version, IReadOnlyCollection<(Guid Tenant, Guid Target)> objects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var wanted = objects.ToHashSet();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyDictionary<AuditValueKey, string?>>(_values.TryGetValue(version, out var values)
                ? values.Where(v => wanted.Contains((v.Key.Tenant, v.Key.Target))).ToDictionary()
                : []);
    }

    /// <inheritdoc />
    public ValueTask<bool> Write(int version, AuditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            if (_checkpoints.GetValueOrDefault(version).Position != batch.From)
                return ValueTask.FromResult(false);
            var rows = _rows.TryGetValue(version, out var r) ? r : _rows[version] = [];
            var values = _values.TryGetValue(version, out var v) ? v : _values[version] = [];
            foreach (var (tenant, obj) in batch.Removed)
                foreach (var key in values.Keys.Where(k => k.Tenant == tenant && k.Target == obj).ToList())
                    values.Remove(key);
            foreach (var (key, value) in batch.Values)
                values[key] = value;
            foreach (var entry in batch.Entries)
                rows[entry.Id] = entry;                                                         // derived ids: a replayed batch writes nothing new
            _checkpoints[version] = new ProjectionCheckpoint(batch.Through, batch.State);
        }
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask Record(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
            _access.Add(entry);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AuditEntry>> Query(int version, Guid tenant, AuditQuery query, (DateTimeOffset At, Guid Id)? after, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            IEnumerable<AuditEntry> all = _access;
            if (_rows.TryGetValue(version, out var rows))
                all = all.Concat(rows.Values);
            var page = all.Where(e => e.Tenant == tenant && Matches(e, query)
                                      && (after is not { } a || e.At < a.At || (e.At == a.At && e.Id.CompareTo(a.Id) < 0)))
                .OrderByDescending(static e => e.At).ThenByDescending(static e => e.Id).Take(take).ToList();
            return ValueTask.FromResult<IReadOnlyList<AuditEntry>>(page);
        }
    }

    internal static bool Matches(AuditEntry e, AuditQuery q)
        => (q.Target is not { } o || e.Target == o) && (q.Actor is not { } actor || e.Actor.Id == actor) && (q.StreamType is not { } st || e.StreamType == st)
           && (q.Field is not { } f || e.Field == f) && (q.Kind is not { } k || e.Kind == k) && (q.From is not { } from || e.At >= from) && (q.To is not { } to || e.At <= to);

    /// <inheritdoc />
    public ValueTask<int> ForgetTenant(Guid tenant, CancellationToken cancellationToken)
    {
        var gone = 0;
        lock (_gate)
        {
            gone += _access.RemoveAll(e => e.Tenant == tenant);
            foreach (var rows in _rows.Values)
                foreach (var id in rows.Values.Where(e => e.Tenant == tenant).Select(static e => e.Id).ToList())
                    gone += rows.Remove(id) ? 1 : 0;
            foreach (var values in _values.Values)
                foreach (var key in values.Keys.Where(k => k.Tenant == tenant).ToList())
                    values.Remove(key);
        }
        return ValueTask.FromResult(gone);
    }

    /// <inheritdoc />
    public ValueTask<int> Purge(DateTimeOffset olderThan, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        var gone = 0;
        lock (_gate)
        {
            foreach (var old in _access.Where(e => e.At < olderThan).Take(batch).ToList())
                gone += _access.Remove(old) ? 1 : 0;
            foreach (var rows in _rows.Values)
                foreach (var id in rows.Values.Where(e => e.At < olderThan).Select(static e => e.Id).Take(batch - gone).ToList())
                    gone += rows.Remove(id) ? 1 : 0;
        }
        return ValueTask.FromResult(gone);
    }
}
