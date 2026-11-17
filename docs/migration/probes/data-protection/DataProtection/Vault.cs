using System.Buffers;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Probe.DataProtection;

/// <summary>A subject whose key was erased: new personal data for it is refused.</summary>
public sealed class SubjectErasedException(Guid subject) : InvalidOperationException($"subject {subject} is erased");

/// <summary>
/// The would-be store integration, over SQL in its own schema (the shipped store cannot be hooked without package changes):
/// <list type="bullet">
/// <item>append: the codec writes the event, <see cref="FieldTransform.Seal"/> turns personal fields into envelopes, the row
/// records its <c>subjects</c>; missing subject keys are created in the same transaction;</item>
/// <item>load: events and their subjects' keys in one round trip (a batch of two statements), keys cached per process;</item>
/// <item>erasure: the subject's key row becomes a tombstone and a NOTIFY evicts it from every process's cache.</item>
/// </list>
/// Keys: a data key per (tenant, subject), wrapped by the tenant's key, wrapped by the master key (<see cref="Kek"/>).
/// </summary>
public sealed class Vault(NpgsqlDataSource db, EventCodec codec, string schema, KeyCache cache, bool subjectsIndex = true)
{
    public const string Channel = "tdp_erased";
    public string Schema { get; } = schema;
    public KeyCache Cache { get; } = cache;
    public int KeyQueries;

    public string Script() => $"""
        DROP SCHEMA IF EXISTS {Schema} CASCADE;
        CREATE SCHEMA {Schema};
        CREATE TABLE {Schema}.streams (tenant uuid NOT NULL, type text NOT NULL, id uuid NOT NULL, version bigint NOT NULL,
            state jsonb NULL, subjects uuid[] NULL, PRIMARY KEY (tenant, type, id));
        CREATE TABLE {Schema}.events (position bigint GENERATED ALWAYS AS IDENTITY, tenant uuid NOT NULL, type text NOT NULL,
            stream_id uuid NOT NULL, version bigint NOT NULL, alias text NOT NULL, alias_version integer NOT NULL, payload jsonb NOT NULL,
            subjects uuid[] NULL, recorded_at timestamptz NOT NULL, PRIMARY KEY (tenant, type, stream_id, version));
        {(subjectsIndex ? $"CREATE INDEX events_subjects ON {Schema}.events USING gin (subjects);" : "")}
        CREATE TABLE {Schema}.tenant_keys (tenant uuid PRIMARY KEY, wrapped bytea NULL, created_at timestamptz NOT NULL, erased_at timestamptz NULL);
        CREATE TABLE {Schema}.subject_keys (tenant uuid NOT NULL, subject uuid NOT NULL, version integer NOT NULL, wrapped bytea NULL,
            created_at timestamptz NOT NULL, erased_at timestamptz NULL, PRIMARY KEY (tenant, subject));
        """;

    // ── keys
    private readonly Dictionary<Guid, byte[]?> _tenantKeys = [];

    private async Task<byte[]?> TenantKey(NpgsqlConnection c, Guid tenant, bool create)
    {
        lock (_tenantKeys) if (_tenantKeys.TryGetValue(tenant, out var k)) return k;
        if (create)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            await using var ins = new NpgsqlCommand($"INSERT INTO {Schema}.tenant_keys VALUES ($1, $2, now(), NULL) ON CONFLICT DO NOTHING", c);
            ins.Parameters.Add(new() { Value = tenant }); ins.Parameters.Add(new() { Value = Kek.Wrap(tenant, Guid.Empty, key) });
            await ins.ExecuteNonQueryAsync();
        }
        await using var sel = new NpgsqlCommand($"SELECT wrapped FROM {Schema}.tenant_keys WHERE tenant = $1", c);
        sel.Parameters.Add(new() { Value = tenant });
        var w = await sel.ExecuteScalarAsync();
        var unwrapped = w is byte[] b ? Kek.Unwrap(tenant, Guid.Empty, b) : null;
        lock (_tenantKeys) _tenantKeys[tenant] = unwrapped;
        return unwrapped;
    }

    public void ForgetTenantKeys() { lock (_tenantKeys) _tenantKeys.Clear(); }

    private static SubjectKey? Unwrap(byte[]? tenantKey, Guid tenant, Guid subject, int version, object wrapped)
    {
        if (tenantKey is null || wrapped is not byte[] w) return null;
        using var aes = new AesGcm(tenantKey, 16);
        var k = new byte[32];
        Span<byte> ad = stackalloc byte[32]; tenant.TryWriteBytes(ad[..16]); subject.TryWriteBytes(ad[16..]);
        aes.Decrypt(w.AsSpan(0, 12), w.AsSpan(28), w.AsSpan(12, 16), k, ad);
        return new SubjectKey(tenant, subject, version, k);
    }

    private static byte[] Wrap(byte[] tenantKey, Guid tenant, Guid subject, byte[] key)
    {
        using var aes = new AesGcm(tenantKey, 16);
        var o = new byte[28 + key.Length];
        RandomNumberGenerator.Fill(o.AsSpan(0, 12));
        Span<byte> ad = stackalloc byte[32]; tenant.TryWriteBytes(ad[..16]); subject.TryWriteBytes(ad[16..]);
        aes.Encrypt(o.AsSpan(0, 12), key, o.AsSpan(28), o.AsSpan(12, 16), ad);
        return o;
    }

    /// <summary>The keys for <paramref name="subjects"/>: cached, read, or (with <paramref name="create"/>) created in <paramref name="tx"/>.</summary>
    public async Task<KeyRing> Keys(NpgsqlConnection c, NpgsqlTransaction? tx, Guid tenant, IReadOnlyCollection<Guid> subjects, bool create)
    {
        var ring = new KeyRing();
        var missing = new List<Guid>();
        foreach (var s in subjects)
            if (Cache.TryGet(tenant, s, out var k)) ring.Add(s, k); else missing.Add(s);
        if (missing.Count == 0) return ring;
        var tk = await TenantKey(c, tenant, create);
        if (create && tk is null) throw new SubjectErasedException(Guid.Empty);
        if (create && tk is not null)
        {
            var keys = missing.Select(s => (s, Wrap(tk, tenant, s, RandomNumberGenerator.GetBytes(32)))).ToArray();
            await using var ins = new NpgsqlCommand($"""
                INSERT INTO {Schema}.subject_keys SELECT $1, s, 1, w, now(), NULL FROM unnest($2::uuid[], $3::bytea[]) AS u(s, w)
                ON CONFLICT DO NOTHING
                """, c, tx);
            ins.Parameters.Add(new() { Value = tenant });
            ins.Parameters.Add(new() { Value = keys.Select(k => k.s).ToArray() });
            ins.Parameters.Add(new() { Value = keys.Select(k => k.Item2).ToArray() });
            await ins.ExecuteNonQueryAsync();
        }
        Interlocked.Increment(ref KeyQueries);
        await using var sel = new NpgsqlCommand($"SELECT subject, version, wrapped FROM {Schema}.subject_keys WHERE tenant = $1 AND subject = ANY($2)", c, tx);
        sel.Parameters.Add(new() { Value = tenant }); sel.Parameters.Add(new() { Value = missing.ToArray() });
        await using (var r = await sel.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var s = r.GetGuid(0);
                var key = Unwrap(tk, tenant, s, r.GetInt32(1), r.GetValue(2));
                ring.Add(s, key); Cache.Put(tenant, s, key);
            }
        foreach (var s in missing)
            if (!ring.TryGet(s, out _)) { ring.Add(s, null); Cache.Put(tenant, s, null); }   // no key row: nothing was ever sealed for it
        return ring;
    }

    // ── append
    public async Task Append<T>(Guid tenant, T aggregate, long expected, bool protect = true, Func<T, PersonalField[]>? state = null)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var changes = aggregate.UncommittedChanges.ToArray();
        var type = codec.StreamTypeOf<T>();
        var id = aggregate.Id.Value;
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var fields = changes.Select(e => protect ? Personal.Of(e.Event, id) : []).ToArray();
        var stateFields = state is null ? [] : protect ? state(aggregate) : [];
        var subjects = fields.SelectMany(f => f).Concat(stateFields).Select(f => f.Subject).Distinct().ToArray();
        var ring = subjects.Length > 0 ? await Keys(c, tx, tenant, subjects, create: true) : KeyRing.Empty;
        foreach (var s in subjects)
            if (ring.TryGet(s, out var k) && k is null) throw new SubjectErasedException(s);
        SubjectKey KeyOf(Guid s) => ring.TryGet(s, out var k) && k is not null ? k : throw new SubjectErasedException(s);

        var aliases = new string[changes.Length]; var versions = new int[changes.Length]; var payloads = new string[changes.Length];
        var subjectsPer = new string?[changes.Length];
        var raw = new ArrayBufferWriter<byte>(512); var sealedBuf = new ArrayBufferWriter<byte>(1024);
        for (var i = 0; i < changes.Length; i++)
        {
            raw.ResetWrittenCount(); sealedBuf.ResetWrittenCount();
            var et = codec.Encode<T>(changes[i].Event, raw);
            aliases[i] = et.Alias; versions[i] = et.Version;
            if (fields[i].Length == 0) payloads[i] = System.Text.Encoding.UTF8.GetString(raw.WrittenSpan);
            else
            {
                FieldTransform.Seal(raw.WrittenSpan, fields[i], KeyOf, sealedBuf);
                payloads[i] = System.Text.Encoding.UTF8.GetString(sealedBuf.WrittenSpan);
            }
            subjectsPer[i] = fields[i].Length == 0 ? null : "{" + string.Join(",", fields[i].Select(f => f.Subject).Distinct()) + "}";
        }
        string? stateJson = null;
        if (state is not null)
        {
            var st = AggregateState.Write(aggregate, codec.Options);
            if (stateFields.Length == 0) stateJson = System.Text.Encoding.UTF8.GetString(st);
            else { sealedBuf.ResetWrittenCount(); FieldTransform.Seal(st, stateFields, KeyOf, sealedBuf); stateJson = System.Text.Encoding.UTF8.GetString(sealedBuf.WrittenSpan); }
        }
        await using (var up = new NpgsqlCommand($"""
            INSERT INTO {Schema}.streams AS s VALUES ($1, $2, $3, $4, $6::jsonb, $7::uuid[])
            ON CONFLICT (tenant, type, id) DO UPDATE SET version = EXCLUDED.version, state = EXCLUDED.state, subjects = EXCLUDED.subjects WHERE s.version = $5
            """, c, tx))
        {
            up.Parameters.Add(new() { Value = tenant }); up.Parameters.Add(new() { Value = type }); up.Parameters.Add(new() { Value = id });
            up.Parameters.Add(new() { Value = expected + changes.Length }); up.Parameters.Add(new() { Value = expected });
            up.Parameters.Add(new() { Value = (object?)stateJson ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            up.Parameters.Add(new() { Value = stateFields.Length == 0 ? DBNull.Value : stateFields.Select(f => f.Subject).Distinct().ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            if (await up.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("conflict");
        }
        await using (var ins = new NpgsqlCommand($"""
            INSERT INTO {Schema}.events (tenant, type, stream_id, version, alias, alias_version, payload, subjects, recorded_at)
            SELECT $1, $2, $3, $4 + o, a, av, p::jsonb, s::uuid[], now()
            FROM unnest($5::text[], $6::int[], $7::text[], $8::text[]) WITH ORDINALITY AS u(a, av, p, s, o)
            """, c, tx))
        {
            ins.Parameters.Add(new() { Value = tenant }); ins.Parameters.Add(new() { Value = type }); ins.Parameters.Add(new() { Value = id });
            ins.Parameters.Add(new() { Value = expected }); ins.Parameters.Add(new() { Value = aliases }); ins.Parameters.Add(new() { Value = versions });
            ins.Parameters.Add(new() { Value = payloads }); ins.Parameters.Add(new() { Value = subjectsPer, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        AggregateChanges.Committed(aggregate);
    }

    // ── load
    public enum KeyFetch { Batch, Separate }

    public async Task<(StreamEvent[] Events, int Lost)> Read(Guid tenant, string type, Guid id, KeyFetch fetch = KeyFetch.Batch)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = new List<(long V, string A, int Av, byte[] P, Guid[]? S)>();
        var ring = new KeyRing();
        var subjects = new HashSet<Guid>();
        if (fetch == KeyFetch.Batch)
        {
            // one round trip: the events, and the keys of their subjects that this process does not have (cache-aware: the
            // statement takes the subjects to skip)
            await using var b = new NpgsqlBatch(c)
            {
                BatchCommands =
                {
                    new($"SELECT version, alias, alias_version, payload, subjects FROM {Schema}.events WHERE tenant = $1 AND type = $2 AND stream_id = $3 ORDER BY version"),
                    new($"""
                        SELECT k.subject, k.version, k.wrapped, t.wrapped FROM {Schema}.subject_keys k JOIN {Schema}.tenant_keys t ON t.tenant = k.tenant
                        WHERE k.tenant = $1 AND k.subject IN (SELECT DISTINCT unnest(subjects) FROM {Schema}.events WHERE tenant = $1 AND type = $2 AND stream_id = $3)
                          AND NOT (k.subject = ANY($4))
                        """),
                },
            };
            foreach (var bc in b.BatchCommands) { bc.Parameters.Add(new NpgsqlParameter { Value = tenant }); bc.Parameters.Add(new NpgsqlParameter { Value = type }); bc.Parameters.Add(new NpgsqlParameter { Value = id }); }
            var cached = Cache.Count == 0 ? [] : CachedSubjects(tenant);
            b.BatchCommands[1].Parameters.Add(new NpgsqlParameter { Value = cached });
            await using var r = await b.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetFieldValue<byte[]>(3), r.IsDBNull(4) ? null : r.GetFieldValue<Guid[]>(4)));
            await r.NextResultAsync();
            while (await r.ReadAsync())
            {
                var s = r.GetGuid(0);
                var tk = r.IsDBNull(3) ? null : Kek.Unwrap(tenant, Guid.Empty, r.GetFieldValue<byte[]>(3));
                var key = Unwrap(tk, tenant, s, r.GetInt32(1), r.GetValue(2));
                ring.Add(s, key); Cache.Put(tenant, s, key);
            }
            foreach (var row in rows) if (row.S is not null) foreach (var s in row.S) subjects.Add(s);
            foreach (var s in subjects)
                if (!ring.TryGet(s, out _))
                {
                    if (Cache.TryGet(tenant, s, out var k)) ring.Add(s, k);
                    else { ring.Add(s, null); Cache.Put(tenant, s, null); }   // tombstone or no row
                }
        }
        else
        {
            await using (var cmd = new NpgsqlCommand($"SELECT version, alias, alias_version, payload, subjects FROM {Schema}.events WHERE tenant = $1 AND type = $2 AND stream_id = $3 ORDER BY version", c))
            {
                cmd.Parameters.Add(new() { Value = tenant }); cmd.Parameters.Add(new() { Value = type }); cmd.Parameters.Add(new() { Value = id });
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetFieldValue<byte[]>(3), r.IsDBNull(4) ? null : r.GetFieldValue<Guid[]>(4)));
            }
            foreach (var row in rows) if (row.S is not null) foreach (var s in row.S) subjects.Add(s);
            if (subjects.Count > 0) ring = await Keys(c, null, tenant, subjects, create: false);
        }
        var lost = 0;
        var events = new StreamEvent[rows.Count];
        var plain = new ArrayBufferWriter<byte>(1024);
        for (var i = 0; i < rows.Count; i++)
        {
            ReadOnlySpan<byte> payload = rows[i].P;
            if (rows[i].S is not null) { plain.ResetWrittenCount(); lost += FieldTransform.Open(payload, ring, plain); payload = plain.WrittenSpan; }
            events[i] = new StreamEvent(rows[i].V, id, Guid.Empty, codec.Decode(type, new EventType(rows[i].A, rows[i].Av), payload));
        }
        return (events, lost);
    }

    private Guid[] CachedSubjects(Guid tenant) => [];   // the probe's batch re-reads keys it has; the cache short-circuits Keys() only

    public async Task<(T Aggregate, int Lost)> Load<T>(Guid tenant, Guid id, KeyFetch fetch = KeyFetch.Batch)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var (events, lost) = await Read(tenant, codec.StreamTypeOf<T>(), id, fetch);
        return (T.Rehydrate((Id<T>)id, events), lost);
    }

    /// <summary>Loads from the sealed state: the stream row and its subjects' keys in one batch (today's store reads the state too).</summary>
    public async Task<(T? Aggregate, int Lost)> LoadState<T>(Guid tenant, Guid id, bool keys = true)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        var type = codec.StreamTypeOf<T>();
        await using var c = await db.OpenConnectionAsync();
        await using var b = new NpgsqlBatch(c)
        {
            BatchCommands =
            {
                new($"SELECT state, subjects FROM {Schema}.streams WHERE tenant = $1 AND type = $2 AND id = $3"),
            },
        };
        if (keys)
            b.BatchCommands.Add(new($"""
                    SELECT k.subject, k.version, k.wrapped, t.wrapped FROM {Schema}.subject_keys k JOIN {Schema}.tenant_keys t ON t.tenant = k.tenant
                    WHERE k.tenant = $1 AND k.subject = ANY((SELECT subjects FROM {Schema}.streams WHERE tenant = $1 AND type = $2 AND id = $3)::uuid[])
                    """));
        foreach (var bc in b.BatchCommands) { bc.Parameters.Add(new NpgsqlParameter { Value = tenant }); bc.Parameters.Add(new NpgsqlParameter { Value = type }); bc.Parameters.Add(new NpgsqlParameter { Value = id }); }
        await using var r = await b.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (null, 0);
        var state = r.GetFieldValue<byte[]>(0);
        var stateSubjects = r.IsDBNull(1) ? null : r.GetFieldValue<Guid[]>(1);
        var ring = new KeyRing();
        if (keys) await r.NextResultAsync();
        while (keys && await r.ReadAsync())
        {
            var s = r.GetGuid(0);
            if (Cache.TryGet(tenant, s, out var cached)) { ring.Add(s, cached); continue; }
            var tk = r.IsDBNull(3) ? null : Kek.Unwrap(tenant, Guid.Empty, r.GetFieldValue<byte[]>(3));
            var key = Unwrap(tk, tenant, s, r.GetInt32(1), r.GetValue(2));
            ring.Add(s, key); Cache.Put(tenant, s, key);
        }
        if (!keys && stateSubjects is not null)
        {
            await r.DisposeAsync();
            ring = await Keys(c, null, tenant, stateSubjects, create: false);   // the cache first; a second round trip only on a miss
        }
        var plain = new ArrayBufferWriter<byte>(1024);
        var lost = FieldTransform.Open(state, ring, plain);
        return (AggregateState.Read((Id<T>)id, plain.WrittenSpan, codec.Options).TryGetValue(out T? a) ? a : null, lost);
    }

    // ── erasure, export
    public async Task Erase(Guid tenant, Guid subject)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"""
            WITH k AS (INSERT INTO {Schema}.subject_keys VALUES ($1, $2, 0, NULL, now(), now())
                       ON CONFLICT (tenant, subject) DO UPDATE SET wrapped = NULL, erased_at = now() RETURNING 1)
            SELECT pg_notify('{Channel}', $1::text || ':' || $2::text) FROM k
            """, c);
        cmd.Parameters.Add(new() { Value = tenant }); cmd.Parameters.Add(new() { Value = subject });
        await cmd.ExecuteNonQueryAsync();
        Cache.Evict(tenant, subject);
    }

    public async Task EraseTenant(Guid tenant)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"UPDATE {Schema}.tenant_keys SET wrapped = NULL, erased_at = now() WHERE tenant = $1", c);
        cmd.Parameters.Add(new() { Value = tenant });
        await cmd.ExecuteNonQueryAsync();
        lock (_tenantKeys) _tenantKeys.Remove(tenant);
        Cache.Clear();
    }

    /// <summary>Every event that holds data of <paramref name="subject"/>, opened: (stream type, stream id, version, JSON).</summary>
    public async Task<List<(string Type, Guid Stream, long Version, string Json)>> Export(Guid tenant, Guid subject)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = new List<(string, Guid, long, byte[])>();
        await using (var cmd = new NpgsqlCommand($"SELECT type, stream_id, version, payload FROM {Schema}.events WHERE tenant = $1 AND subjects @> ARRAY[$2] ORDER BY position", c))
        {
            cmd.Parameters.Add(new() { Value = tenant }); cmd.Parameters.Add(new() { Value = subject });
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetGuid(1), r.GetInt64(2), r.GetFieldValue<byte[]>(3)));
        }
        var ring = await Keys(c, null, tenant, [subject], create: false);
        var plain = new ArrayBufferWriter<byte>(1024);
        return [.. rows.Select(x => { plain.ResetWrittenCount(); FieldTransform.Open(x.Item4, ring, plain); return (x.Item1, x.Item2, x.Item3, System.Text.Encoding.UTF8.GetString(plain.WrittenSpan)); })];
    }
}
