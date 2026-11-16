using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Tarol.Persistence;

namespace Probe.Queries;

public interface IAuctionQueries
{
    Task<Page<AuctionRow>> Search(Guid tenant, AuctionSearch q, CancellationToken ct);
}

/// <summary>In memory: the ProjectionTest store (rows + checkpoint), and the same query semantics in LINQ.</summary>
public sealed class InMemoryAuctionTable : IReadModelStore<AuctionRow>, IAuctionQueries
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid, Guid), Versioned<AuctionRow>> _rows = [];
    private GlobalPosition _checkpoint;

    public int Count { get { lock (_gate) return _rows.Count; } }
    public Task Ensure(CancellationToken ct) => Task.CompletedTask;
    public Task<GlobalPosition> Checkpoint(CancellationToken ct) { lock (_gate) return Task.FromResult(_checkpoint); }

    public Task<Dictionary<(Guid, Guid), Versioned<AuctionRow>>> Load(IReadOnlyCollection<(Guid Tenant, Guid Id)> keys, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(keys.Where(_rows.ContainsKey).ToDictionary(k => k, k => _rows[k]));
    }

    public Task<bool> Write(IReadOnlyList<Versioned<AuctionRow>> rows, GlobalPosition from, GlobalPosition to, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_checkpoint != from)
                return Task.FromResult(false);
            foreach (var r in rows)
                if (!_rows.TryGetValue((r.Tenant, r.Id), out var old) || old.Version < r.Version)
                    _rows[(r.Tenant, r.Id)] = r;
            _checkpoint = to;
            return Task.FromResult(true);
        }
    }

    public Task<Page<AuctionRow>> Search(Guid tenant, AuctionSearch q, CancellationToken ct)
    {
        List<Versioned<AuctionRow>> all;
        lock (_gate)
            all = [.. _rows.Values.Where(r => r.Tenant == tenant && (q.Status is null || r.Row.Status == q.Status) && (q.Seller is null || r.Row.Seller == q.Seller))];
        var ordered = q.Sort == AuctionSort.EndsAt
            ? all.OrderBy(r => r.Row.EndsAt).ThenBy(r => r.Id)
            : all.OrderByDescending(r => r.Row.HighestBid).ThenByDescending(r => r.Id);
        IEnumerable<Versioned<AuctionRow>> seq = ordered;
        var filters = Cursor.Filters(q);
        if (q.After is { } after)
        {
            if (!Cursor.TryDecode(after, q.Sort, filters, out var value, out var id))
                throw new ArgumentException("bad cursor");
            seq = q.Sort == AuctionSort.EndsAt
                ? seq.Where(r => r.Row.EndsAt.UtcTicks > long.Parse(value, CultureInfo.InvariantCulture)
                              || (r.Row.EndsAt.UtcTicks == long.Parse(value, CultureInfo.InvariantCulture) && r.Id.CompareTo(id) > 0))
                : seq.Where(r => r.Row.HighestBid < decimal.Parse(value, CultureInfo.InvariantCulture)
                              || (r.Row.HighestBid == decimal.Parse(value, CultureInfo.InvariantCulture) && r.Id.CompareTo(id) < 0));
        }
        else if (q.Offset is { } offset)
            seq = seq.Skip(offset);
        var page = seq.Take(Math.Clamp(q.Limit, 1, AuctionSearch.MaxLimit) + 1).ToList();
        return Task.FromResult(Paging.ToPage(page, q, filters));
    }
}

internal static class Paging
{
    public static Page<AuctionRow> ToPage(List<Versioned<AuctionRow>> fetched, AuctionSearch q, uint filters)
    {
        var limit = Math.Clamp(q.Limit, 1, AuctionSearch.MaxLimit);
        var more = fetched.Count > limit;
        var items = more ? fetched[..limit] : fetched;
        var last = items.Count > 0 ? items[^1] : null;
        var next = more && last is not null ? Cursor.Encode(q.Sort, filters, Cursor.SortValue(q.Sort, last.Row), last.Id) : null;
        return new Page<AuctionRow>([.. items.Select(i => i.Row)], next, Cursor.ETag(items.Select(i => (i.Id, i.Version))));
    }
}

/// <summary>
/// PostgreSQL (candidate R-C): a table per projection version with typed query columns + the row as jsonb, written by the
/// host from the row (generated column list); the checkpoint row in the same transaction (exactly-once effect).
/// </summary>
public sealed class NpgsqlAuctionTable : IReadModelStore<AuctionRow>, IAuctionQueries
{
    private readonly NpgsqlDataSource _db;
    private readonly string _s, _t, _name;
    private readonly int _version;

    public NpgsqlAuctionTable(NpgsqlDataSource db, string schema, string name, int version, string? table = null)
        => (_db, _s, _name, _version, _t) = (db, schema, name, version, $"{schema}.{table ?? $"rm_{name.Replace('-', '_')}_v{version}"}");

    public string Table => _t;
    /// <summary>A2: write rows in their own statement and the checkpoint after it (at-least-once, idempotent by version).</summary>
    public bool CheckpointInTransaction { get; init; } = true;

    public string Ddl => $"""
        CREATE TABLE IF NOT EXISTS {_s}.projections (
            name text NOT NULL, version integer NOT NULL, tx xid8 NOT NULL DEFAULT '0', pos bigint NOT NULL DEFAULT 0,
            updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (name, version));
        INSERT INTO {_s}.projections (name, version) VALUES ('{_name}', {_version}) ON CONFLICT DO NOTHING;
        CREATE TABLE IF NOT EXISTS {_t} (
            tenant uuid NOT NULL, id uuid NOT NULL, version bigint NOT NULL,
            title text NOT NULL, seller text NOT NULL, ends_at timestamptz NOT NULL, status smallint NOT NULL,
            highest_bid numeric NOT NULL, bids integer NOT NULL, doc jsonb NOT NULL,
            PRIMARY KEY (tenant, id));
        CREATE INDEX IF NOT EXISTS {_t.Replace('.', '_')}_ends ON {_t} (tenant, ends_at, id);
        CREATE INDEX IF NOT EXISTS {_t.Replace('.', '_')}_status_ends ON {_t} (tenant, status, ends_at, id);
        CREATE INDEX IF NOT EXISTS {_t.Replace('.', '_')}_highest ON {_t} (tenant, highest_bid, id);
        """;

    public async Task Ensure(CancellationToken ct)
    {
        await using var c = _db.CreateCommand(Ddl);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<GlobalPosition> Checkpoint(CancellationToken ct)
    {
        await using var c = _db.CreateCommand($"SELECT tx::text, pos FROM {_s}.projections WHERE name = $1 AND version = $2");
        c.Parameters.Add(new() { Value = _name });
        c.Parameters.Add(new() { Value = _version });
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new GlobalPosition(ulong.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetInt64(1)) : default;
    }

    public async Task<Dictionary<(Guid, Guid), Versioned<AuctionRow>>> Load(IReadOnlyCollection<(Guid Tenant, Guid Id)> keys, CancellationToken ct)
    {
        await using var c = _db.CreateCommand($"SELECT tenant, id, version, doc::text FROM {_t} WHERE (tenant, id) IN (SELECT * FROM unnest($1::uuid[], $2::uuid[]))");
        c.Parameters.Add(new() { Value = keys.Select(k => k.Tenant).ToArray() });
        c.Parameters.Add(new() { Value = keys.Select(k => k.Id).ToArray() });
        var result = new Dictionary<(Guid, Guid), Versioned<AuctionRow>>(keys.Count);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = JsonSerializer.Deserialize(r.GetString(3), ProbeJson.Default.AuctionRow)!;
            result[(r.GetGuid(0), r.GetGuid(1))] = new(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), row);
        }
        return result;
    }

    private string Upsert => $"""
        INSERT INTO {_t} AS t (tenant, id, version, title, seller, ends_at, status, highest_bid, bids, doc)
        SELECT * FROM unnest($1::uuid[], $2::uuid[], $3::bigint[], $4::text[], $5::text[], $6::timestamptz[], $7::smallint[], $8::numeric[], $9::integer[], $10::jsonb[])
        ON CONFLICT (tenant, id) DO UPDATE SET version = excluded.version, title = excluded.title, seller = excluded.seller,
            ends_at = excluded.ends_at, status = excluded.status, highest_bid = excluded.highest_bid, bids = excluded.bids, doc = excluded.doc
        WHERE t.version < excluded.version
        """;

    private void AddRows(NpgsqlBatchCommand c, IReadOnlyList<Versioned<AuctionRow>> rows)
    {
        c.Parameters.Add(new() { Value = rows.Select(r => r.Tenant).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Id).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Version).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Row.Title).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Row.Seller).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Row.EndsAt.ToUniversalTime()).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => (short)r.Row.Status).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Row.HighestBid).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => r.Row.Bids).ToArray() });
        c.Parameters.Add(new() { Value = rows.Select(r => JsonSerializer.Serialize(r.Row, ProbeJson.Default.AuctionRow)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
    }

    private NpgsqlBatchCommand Advance(GlobalPosition from, GlobalPosition to)
    {
        var c = new NpgsqlBatchCommand($"UPDATE {_s}.projections SET tx = $3::text::xid8, pos = $4, updated_at = now() WHERE name = $1 AND version = $2 AND tx = $5::text::xid8 AND pos = $6");
        foreach (var v in new object[] { _name, _version, to.Transaction.ToString(CultureInfo.InvariantCulture), to.Position, from.Transaction.ToString(CultureInfo.InvariantCulture), from.Position })
            c.Parameters.Add(new() { Value = v });
        return c;
    }

    public async Task<bool> Write(IReadOnlyList<Versioned<AuctionRow>> rows, GlobalPosition from, GlobalPosition to, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        if (!CheckpointInTransaction)
        {
            if (rows.Count > 0)
            {
                await using var b0 = new NpgsqlBatch(connection);
                var up = new NpgsqlBatchCommand(Upsert); AddRows(up, rows); b0.BatchCommands.Add(up);
                await b0.ExecuteNonQueryAsync(ct);
            }
            await using var b1 = new NpgsqlBatch(connection);
            b1.BatchCommands.Add(Advance(from, to));
            return await b1.ExecuteNonQueryAsync(ct) == 1;
        }
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var guard = new NpgsqlBatch(connection, tx))
        {
            guard.BatchCommands.Add(Advance(from, to));               // first: the row lock serializes concurrent runners
            if (await guard.ExecuteNonQueryAsync(ct) != 1)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }
        if (rows.Count > 0)
        {
            await using var batch = new NpgsqlBatch(connection, tx);
            var up = new NpgsqlBatchCommand(Upsert); AddRows(up, rows); batch.BatchCommands.Add(up);
            await batch.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>The "generated" query: filters as parameters, the sort from a whitelist (never text from the request).</summary>
    public async Task<Page<AuctionRow>> Search(Guid tenant, AuctionSearch q, CancellationToken ct)
    {
        var limit = Math.Clamp(q.Limit, 1, AuctionSearch.MaxLimit);
        var filters = Cursor.Filters(q);
        var sql = new StringBuilder($"SELECT id, version, doc::text FROM {_t} WHERE tenant = $1");
        await using var c = _db.CreateCommand();
        c.Parameters.Add(new() { Value = tenant });
        if (q.Status is { } status) { c.Parameters.Add(new() { Value = (short)status }); sql.Append(CultureInfo.InvariantCulture, $" AND status = ${c.Parameters.Count}"); }
        if (q.Seller is { } seller) { c.Parameters.Add(new() { Value = seller }); sql.Append(CultureInfo.InvariantCulture, $" AND seller = ${c.Parameters.Count}"); }
        if (q.After is { } after)
        {
            if (!Cursor.TryDecode(after, q.Sort, filters, out var value, out var id))
                throw new ArgumentException("bad cursor");
            if (q.Sort == AuctionSort.EndsAt)
                c.Parameters.Add(new() { Value = new DateTimeOffset(long.Parse(value, CultureInfo.InvariantCulture), TimeSpan.Zero) });
            else
                c.Parameters.Add(new() { Value = decimal.Parse(value, CultureInfo.InvariantCulture) });
            c.Parameters.Add(new() { Value = id });
            sql.Append(q.Sort == AuctionSort.EndsAt
                ? $" AND (ends_at, id) > (${c.Parameters.Count - 1}, ${c.Parameters.Count})"
                : $" AND (highest_bid, id) < (${c.Parameters.Count - 1}, ${c.Parameters.Count})");
        }
        sql.Append(q.Sort == AuctionSort.EndsAt ? " ORDER BY ends_at, id" : " ORDER BY highest_bid DESC, id DESC");
        sql.Append(CultureInfo.InvariantCulture, $" LIMIT {limit + 1}");
        if (q.After is null && q.Offset is { } offset)
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET {Math.Max(0, offset)}");
        c.CommandText = sql.ToString();
        var fetched = new List<Versioned<AuctionRow>>(limit + 1);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            fetched.Add(new(tenant, r.GetGuid(0), r.GetInt64(1), JsonSerializer.Deserialize(r.GetString(2), ProbeJson.Default.AuctionRow)!));
        return Paging.ToPage(fetched, q, filters);
    }
}

/// <summary>Wake-up by LISTEN/NOTIFY: one connection, a signal; the probe's trigger on events stands in for a store option.</summary>
public sealed class Listener : IAsyncDisposable
{
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public Listener(NpgsqlDataSource db, string channel) => _loop = Loop(db, channel);

    private async Task Loop(NpgsqlDataSource db, string channel)
    {
        await using var connection = await db.OpenConnectionAsync(_stop.Token);
        connection.Notification += (_, _) => { if (_signal.CurrentCount == 0) _signal.Release(); };
        await using (var listen = new NpgsqlCommand($"LISTEN {channel}", connection))
            await listen.ExecuteNonQueryAsync(_stop.Token);
        _signal.Release();
        try { while (true) await connection.WaitAsync(_stop.Token); }
        catch (OperationCanceledException) { }
    }

    public async Task Wait(TimeSpan max, CancellationToken ct) => await _signal.WaitAsync(max, ct);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
