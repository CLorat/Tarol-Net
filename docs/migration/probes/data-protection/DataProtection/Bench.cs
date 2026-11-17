using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Queries.Npgsql;

namespace Probe.DataProtection;

internal static class Bench
{
    public static async Task Run(string[] args)
    {
        int N(int i, int d) => args.Length > i ? int.Parse(args[i], CultureInfo.InvariantCulture) : d;
        switch (args)
        {
            case ["crypto"]: Crypto(); break;
            case ["codec"]: Codec(); break;
            case ["load", ..]: await Load(N(1, 2000)); break;
            case ["commit", ..]: await Commit(N(1, 2000)); break;
            case ["export", ..]: await Export(N(1, 1_000_000)); break;
            case ["forget", ..]: await Forget(N(1, 20_000)); break;
            case ["retention", ..]: await Retention(N(1, 100_000)); break;
            default: Console.WriteLine("bench <crypto|codec|load [streams]|commit [per writer]|export [events]|forget [streams]|retention [streams]>"); break;
        }
    }

    private static SubjectKey NewKey(Guid subject) => new(Lab.TenantA, subject, 1, RandomNumberGenerator.GetBytes(32));

    // ── (a) the primitives
    private static void Crypto()
    {
        var key = NewKey(Guid.NewGuid());
        var ring = new KeyRing(); ring.Add(key.Subject, key);
        Console.WriteLine("| operation | per call |"); Console.WriteLine("|---|---|");
        foreach (var size in new[] { 32, 256 })
        {
            var plain = RandomNumberGenerator.GetBytes(size);
            var env = new byte[Envelope.MaxLength(size)];
            var n = Envelope.Seal(key, plain, env);
            var outBuf = new ArrayBufferWriter<byte>(512);
            var seal = Lab.Ns(() => Envelope.Seal(key, plain, env), 200_000);
            var open = Lab.Ns(() => { outBuf.ResetWrittenCount(); Envelope.Open(ring, env.AsSpan(0, n), outBuf, out _); }, 200_000);
            Console.WriteLine($"| seal a {size}-byte value (AES-256-GCM, random nonce, base64url envelope of {n} chars) | {Lab.F(seal)} ns |");
            Console.WriteLine($"| open it | {Lab.F(open)} ns |");
        }
        var tk = RandomNumberGenerator.GetBytes(32);
        Console.WriteLine($"| new AesGcm(key) (per unwrapped key; cache it) | {Lab.F(Lab.Ns(() => { using var a = new AesGcm(tk, 16); }, 200_000))} ns |");
        var wrapped = Kek.Wrap(Lab.TenantA, key.Subject, tk);
        Console.WriteLine($"| unwrap a data key (master → key, AES-GCM of 32 bytes incl. AesGcm construction) | {Lab.F(Lab.Ns(() => Kek.Unwrap(Lab.TenantA, key.Subject, wrapped), 200_000))} ns |");
        Console.WriteLine($"| blind index of an email (HMAC-SHA256, normalized) | {Lab.F(Lab.Ns(() => BlindIndex.Of(tk, "alice@example.org"), 200_000))} ns |");
        Console.WriteLine($"| RandomNumberGenerator.GetBytes(32) (a new data key) | {Lab.F(Lab.Ns(() => RandomNumberGenerator.GetBytes(32), 200_000))} ns |");
    }

    // ── (b) the codec candidates on one event with 2 personal fields of 3
    private static void Codec()
    {
        var codec = Lab.Codec();
        var subject = Guid.NewGuid();
        var key = NewKey(subject);
        var ring = new KeyRing(); ring.Add(subject, key);
        var gone = new KeyRing(); gone.Add(subject, null);
        DomainEvent e = new Customer.Registered("Alice Example", "alice@example.org", "DE");
        var d = new RegisteredD("Alice Example", "alice@example.org", "DE");
        PersonalField[] fields = [new("name", subject), new("email", subject)];
        var raw = new ArrayBufferWriter<byte>(512); var sealedA = new ArrayBufferWriter<byte>(1024); var outBuf = new ArrayBufferWriter<byte>(1024);
        codec.Encode<Customer>(e, raw);
        var plainJson = raw.WrittenSpan.ToArray();
        FieldTransform.Seal(plainJson, fields, _ => key, sealedA); var a = sealedA.WrittenSpan.ToArray();
        var bBuf = new ArrayBufferWriter<byte>(1024); NodeTransform.Seal(plainJson, fields, _ => key, bBuf); var bJson = bBuf.WrittenSpan.ToArray();
        var cBuf = new ArrayBufferWriter<byte>(1024); WholeTransform.Seal(plainJson, key, cBuf); var c = cBuf.WrittenSpan.ToArray();
        PersonalString.SealWith = key; PersonalString.OpenWith = ring;
        var dJson = JsonSerializer.SerializeToUtf8Bytes(d, ProbeJson.Default.RegisteredD);
        var stream = codec.StreamTypeOf<Customer>();
        var et = new EventType(Customer.EventTypes.Events.Single(x => x.ClrType == typeof(Customer.Registered)).Type.Alias, 1);
        const int n = 200_000;
        double Enc(Action act) => Lab.Ns(act, n);
        Console.WriteLine($"| candidate | encode (event → stored JSON) | decode (stored JSON → event) | decode, key erased | stored size |");
        Console.WriteLine("|---|---|---|---|---|");
        Console.WriteLine($"| plain (today's codec, nothing sealed) | {Lab.F(Enc(() => { raw.ResetWrittenCount(); codec.Encode<Customer>(e, raw); }))} ns | {Lab.F(Enc(() => codec.Decode(stream, et, plainJson)))} ns | – | {plainJson.Length} B |");
        Console.WriteLine($"| A: transform after the serializer (Utf8JsonReader/Writer, raw slices) | {Lab.F(Enc(() => { raw.ResetWrittenCount(); sealedA.ResetWrittenCount(); codec.Encode<Customer>(e, raw); FieldTransform.Seal(raw.WrittenSpan, fields, _ => key, sealedA); }))} ns"
            + $" | {Lab.F(Enc(() => { outBuf.ResetWrittenCount(); FieldTransform.Open(a, ring, outBuf); codec.Decode(stream, et, outBuf.WrittenSpan); }))} ns"
            + $" | {Lab.F(Enc(() => { outBuf.ResetWrittenCount(); FieldTransform.Open(a, gone, outBuf); codec.Decode(stream, et, outBuf.WrittenSpan); }))} ns | {a.Length} B |");
        Console.WriteLine($"| B: transform through JsonNode | {Lab.F(Enc(() => { raw.ResetWrittenCount(); bBuf.ResetWrittenCount(); codec.Encode<Customer>(e, raw); NodeTransform.Seal(raw.WrittenSpan, fields, _ => key, bBuf); }))} ns"
            + $" | {Lab.F(Enc(() => { outBuf.ResetWrittenCount(); NodeTransform.Open(bJson, ring, outBuf); codec.Decode(stream, et, outBuf.WrittenSpan); }))} ns"
            + $" | {Lab.F(Enc(() => { outBuf.ResetWrittenCount(); NodeTransform.Open(bJson, gone, outBuf); codec.Decode(stream, et, outBuf.WrittenSpan); }))} ns | {bJson.Length} B |");
        var cOut = new ArrayBufferWriter<byte>(512);
        Console.WriteLine($"| C: the whole payload in one envelope | {Lab.F(Enc(() => { raw.ResetWrittenCount(); cBuf.ResetWrittenCount(); codec.Encode<Customer>(e, raw); WholeTransform.Seal(raw.WrittenSpan, key, cBuf); }))} ns"
            + $" | {Lab.F(Enc(() => { cOut.ResetWrittenCount(); WholeTransform.Open(c, ring, cOut); codec.Decode(stream, et, cOut.WrittenSpan); }))} ns"
            + $" | the whole event is lost (country too) | {c.Length} B |");
        Console.WriteLine($"| D: converter in the serializer, key in a thread-static scope | {Lab.F(Enc(() => JsonSerializer.SerializeToUtf8Bytes(d, ProbeJson.Default.RegisteredD)))} ns"
            + $" | {Lab.F(Enc(() => JsonSerializer.Deserialize(dJson, ProbeJson.Default.RegisteredD)))} ns"
            + $" | {Lab.F(Enc(() => { PersonalString.OpenWith = gone; JsonSerializer.Deserialize(dJson, ProbeJson.Default.RegisteredD); PersonalString.OpenWith = ring; }))} ns | {dJson.Length} B |");
        var subjects = new HashSet<Guid>();
        Lab.Info($"finding the subjects of a sealed payload without a subjects column (scan for envelopes): {Lab.F(Lab.Ns(() => { subjects.Clear(); FieldTransform.Subjects(a, subjects); }, n))} ns per event");
        Lab.Info("A's stored JSON: " + System.Text.Encoding.UTF8.GetString(a));
    }

    // ── (c) loads: today's store, the vault without and with protection, warm and cold keys, an erased subject
    private static async Task Load(int streams)
    {
        var codec = Lab.Codec();
        var store = await Lab.Store("dpl_store", fresh: true);
        var plain = new Vault(Lab.Db, codec, "dpl_plain", new KeyCache(TimeSpan.FromMinutes(5)));
        var prot = new Vault(Lab.Db, codec, "dpl_prot", new KeyCache(TimeSpan.FromMinutes(5)));
        await Lab.Exec(plain.Script()); await Lab.Exec(prot.Script());
        var ids = new Guid[streams]; var plainIds = new Guid[streams];
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) is var i && i < streams)
            {
                Customer Make(Guid? id = null)
                {
                    var c = Customer.Register(Lab.Time.GetUtcNow(), $"Customer {i}", $"c{i}@example.org", "DE");
                    for (var m = 0; m < 8; m++) _ = c.Move($"{m} Main Street", "Berlin");
                    _ = c.Promote();
                    return c;
                }
                var c1 = Make(); ids[i] = c1.Id.Value;
                await prot.Append(Lab.TenantA, c1, 0, state: Personal.State);
                var c2 = Make(); plainIds[i] = c2.Id.Value;
                await plain.Append(Lab.TenantA, c2, 0, protect: false, state: Personal.State);
                await Lab.Commit(store, Make(), 0, Lab.Context(Lab.TenantA));
            }
        })));
        var storeIds = await Ids("dpl_store");
        var erased = ids.Take(streams / 10).ToArray();
        Lab.Info($"{Lab.N(streams)} customers × 10 events (9 with personal fields: 2 + 8 × 1) in each table; the store's copy has its state in streams.state");
        Console.WriteLine("| load of one customer (10 events) | median | p99 | key queries per load |");
        Console.WriteLine("|---|---|---|---|");
        var rnd = new Random(7);
        var rows = new List<(string What, Func<Guid, Task> Load, Guid[] Pool, Vault? V, bool Cold)>();
        void Row(string what, Func<Guid, Task> load, Guid[] pool, Vault? v = null, bool cold = false) => rows.Add((what, load, pool, v, cold));
        async Task Measure()
        {
            var best = rows.Select(_ => (P50: double.MaxValue, P99: double.MaxValue, Q: 0.0)).ToArray();
            for (var pass = 0; pass < 3; pass++)   // the rows interleaved, 3 passes; the best pass per row (the first runs in tier 0)
                for (var k = 0; k < rows.Count; k++)
                {
                    var (_, load, pool, v, cold) = rows[k];
                    for (var w = 0; w < 100; w++) await load(pool[rnd.Next(pool.Length)]);
                    var t = new List<double>(); var q0 = v?.KeyQueries ?? 0;
                    for (var i = 0; i < 1000; i++)
                    {
                        var id = pool[rnd.Next(pool.Length)];
                        if (cold) { v!.Cache.Clear(); v.ForgetTenantKeys(); }
                        var sw = Stopwatch.StartNew(); await load(id); t.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    t.Sort();
                    if (Lab.Pct(t, 0.5) < best[k].P50) best[k] = (Lab.Pct(t, 0.5), Lab.Pct(t, 0.99), v is null ? -1 : (v.KeyQueries - q0) / 1000.0);
                }
            for (var k = 0; k < rows.Count; k++)
                Console.WriteLine($"| {rows[k].What} | {Lab.F(best[k].P50)} ms | {Lab.F(best[k].P99)} ms | {(best[k].Q < 0 ? "–" : Lab.F(best[k].Q))} |");
            rows.Clear();
        }
        Row("today's NpgsqlAggregateStore.Load (reads streams.state)", async id => await store.Load((Tarol.StronglyTypedId.Id<Customer>)id, Lab.Context(Lab.TenantA), default), storeIds);
        Row("probe: the plain state (1 row, no envelopes, no key statement)", id => plain.LoadState<Customer>(Lab.TenantA, id, keys: false), plainIds);
        Row("protected state: the row and its subjects' keys in one batch, cold cache", id => prot.LoadState<Customer>(Lab.TenantA, id), ids, prot, cold: true);
        Row("protected state, keys in the same batch, cached key (no unwrap)", id => prot.LoadState<Customer>(Lab.TenantA, id), ids);
        Row("protected state, cached key (a separate fetch only on a miss): hit", id => prot.LoadState<Customer>(Lab.TenantA, id, keys: false), ids, prot);
        Row("protected state, separate fetch, cold cache (2 round trips + unwrap)", id => prot.LoadState<Customer>(Lab.TenantA, id, keys: false), ids, prot, cold: true);
        Row("probe: replay 10 plain events (no envelopes, no key statement)", id => plain.Load<Customer>(Lab.TenantA, id, Vault.KeyFetch.Separate), plainIds);
        Row("protected replay, keys in the same batch (1 round trip)", id => prot.Load<Customer>(Lab.TenantA, id, Vault.KeyFetch.Batch), ids);
        Row("protected replay, cached key (a separate fetch only on a miss): hit", id => prot.Load<Customer>(Lab.TenantA, id, Vault.KeyFetch.Separate), ids, prot);
        Row("protected replay, separate fetch, cold cache (2 round trips + unwrap)", id => prot.Load<Customer>(Lab.TenantA, id, Vault.KeyFetch.Separate), ids, prot, cold: true);
        await Measure();
        foreach (var s in erased) await prot.Erase(Lab.TenantA, s);
        Row("protected replay, the subject erased (9 fields read as null; the tombstone cached)", id => prot.Load<Customer>(Lab.TenantA, id, Vault.KeyFetch.Separate), erased);
        Row("protected state, the subject erased (3 fields read as null; the tombstone cached)", id => prot.LoadState<Customer>(Lab.TenantA, id, keys: false), erased);
        await Measure();
        var size = await Lab.Scalar<string>("SELECT pg_size_pretty(pg_total_relation_size('dpl_plain.events')) || ' plain vs ' || pg_size_pretty(pg_total_relation_size('dpl_prot.events')) || ' protected (incl. the subjects GIN index ' || pg_size_pretty(pg_relation_size('dpl_prot.events_subjects')) || ')'");
        Lab.Info("events table size: " + size);
    }

    private static async Task<Guid[]> Ids(string schema)
    {
        var ids = new List<Guid>();
        await using var c = Lab.Db.CreateCommand($"SELECT id FROM {schema}.streams");
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        return [.. ids];
    }

    // ── (d) commits with and without protection (4 writers)
    private static async Task Commit(int perWriter)
    {
        var codec = Lab.Codec();
        Console.WriteLine("| commit (4 writers) | new customer (Register: 1 event, 2 sealed fields, a new key) p50 / p99 | existing customer (Move: 1 event, 1 sealed field, key cached) p50 / p99 |");
        Console.WriteLine("|---|---|---|");
        foreach (var (label, protect, gin) in new[] { ("probe, plain (no subjects, no envelopes)", false, false), ("protected, subjects column without index", true, false), ("protected, subjects column + GIN index", true, true) })
        {
            var v = new Vault(Lab.Db, codec, "dpc", new KeyCache(TimeSpan.FromMinutes(5)), gin);
            await Lab.Exec(v.Script());
            var created = new List<double>(); var moved = new List<double>();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
            {
                var mine = new List<(Customer C, long V)>();
                var c1 = new List<double>(); var c2 = new List<double>();
                for (var i = 0; i < perWriter; i++)
                {
                    var c = Customer.Register(Lab.Time.GetUtcNow(), $"Customer {i}", $"c{i}@example.org", "DE");
                    var sw = Stopwatch.StartNew(); await v.Append(Lab.TenantA, c, 0, protect, Personal.State); c1.Add(sw.Elapsed.TotalMilliseconds);
                    mine.Add((c, 1));
                }
                for (var i = 0; i < perWriter; i++)
                {
                    var (c, ver) = mine[i];
                    _ = c.Move($"{i} Road", "Berlin");
                    var sw = Stopwatch.StartNew(); await v.Append(Lab.TenantA, c, ver, protect, Personal.State); c2.Add(sw.Elapsed.TotalMilliseconds);
                }
                lock (created) { created.AddRange(c1.Skip(50)); moved.AddRange(c2.Skip(50)); }
            })));
            created.Sort(); moved.Sort();
            Console.WriteLine($"| {label} | {Lab.F(Lab.Pct(created, 0.5))} / {Lab.F(Lab.Pct(created, 0.99))} ms | {Lab.F(Lab.Pct(moved, 0.5))} / {Lab.F(Lab.Pct(moved, 0.99))} ms |");
        }
        var store = await Lab.Store("dpc_store", fresh: true);
        var t = new List<double>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            var mine = new List<double>();
            for (var i = 0; i < perWriter; i++)
            {
                var c = Customer.Register(Lab.Time.GetUtcNow(), $"Customer {i}", $"c{i}@example.org", "DE");
                var sw = Stopwatch.StartNew(); await Lab.Commit(store, c, 0, Lab.Context(Lab.TenantA)); mine.Add(sw.Elapsed.TotalMilliseconds);
            }
            lock (t) t.AddRange(mine.Skip(50));
        })));
        t.Sort();
        Lab.Info($"reference: today's NpgsqlAggregateStore.Append of a new customer (state, events, 4 writers): p50 {Lab.F(Lab.Pct(t, 0.5))} ms, p99 {Lab.F(Lab.Pct(t, 0.99))} ms");
    }

    // ── (e) finding a subject's events among many (export, erasure's re-fold)
    private static async Task Export(int events)
    {
        const string s = "dpx";
        var subjects = events / 10;
        await Lab.Exec($"""
            DROP SCHEMA IF EXISTS {s} CASCADE; CREATE SCHEMA {s};
            CREATE TABLE {s}.events (position bigint GENERATED ALWAYS AS IDENTITY, tenant uuid NOT NULL, type text NOT NULL, stream_id uuid NOT NULL,
                version bigint NOT NULL, payload jsonb NOT NULL, subjects uuid[] NULL, PRIMARY KEY (tenant, type, stream_id, version));
            CREATE TABLE {s}.subject_streams (tenant uuid NOT NULL, subject uuid NOT NULL, type text NOT NULL, stream_id uuid NOT NULL, PRIMARY KEY (tenant, subject, type, stream_id));
            """);
        // 1/10 of the events hold no personal data; the rest hold one subject: its own stream's (customers) or a customer's (orders)
        await Lab.Exec($"""
            INSERT INTO {s}.events (tenant, type, stream_id, version, payload, subjects)
            SELECT '{Lab.TenantA}', CASE WHEN g % 3 = 0 THEN 'order' ELSE 'customer' END, md5((g / 10)::text)::uuid, g % 10 + 1,
                   jsonb_build_object('name', 'tdp1.' || md5(g::text) || md5((g+1)::text), 'country', 'DE'),
                   CASE WHEN g % 10 = 9 THEN NULL ELSE ARRAY[md5(('s' || (g % {subjects}))::text)::uuid] END
            FROM generate_series(0, {events - 1}) g
            """);
        await Lab.Exec($"INSERT INTO {s}.subject_streams SELECT DISTINCT tenant, unnest(subjects), type, stream_id FROM {s}.events WHERE subjects IS NOT NULL");
        await Lab.Exec($"ANALYZE {s}.events; ANALYZE {s}.subject_streams");
        var probe = await Lab.Scalar<string>($"SELECT md5('s42')::uuid::text");
        var sub = Guid.Parse(probe);
        var hits = await Lab.Scalar<long>($"SELECT count(*) FROM {s}.events WHERE subjects @> ARRAY[$1]", sub);
        Lab.Info($"{Lab.N(events)} events, {Lab.N(subjects)} subjects; the subject probed is in {hits} events; table {await Lab.Scalar<string>($"SELECT pg_size_pretty(pg_total_relation_size('{s}.events'))")}");
        Console.WriteLine("| find one subject's events | median | index size | build |"); Console.WriteLine("|---|---|---|---|");
        var noIdx = await Lab.Median(() => Lab.Exec($"SELECT * FROM {s}.events WHERE tenant = $1 AND subjects @> ARRAY[$2]", Lab.TenantA, sub), 5);
        Console.WriteLine($"| subjects @> ARRAY[s], no index (sequential scan) | {Lab.F(noIdx)} ms | – | – |");
        var like = await Lab.Median(() => Lab.Exec($"SELECT * FROM {s}.events WHERE payload::text LIKE '%' || $1 || '%'", probe), 3);
        Console.WriteLine($"| payload::text LIKE '%subject%' (no subjects column) | {Lab.F(like)} ms | – | – |");
        var sw = Stopwatch.StartNew();
        await Lab.Exec($"CREATE INDEX events_subjects ON {s}.events USING gin (subjects)");
        var build = sw.Elapsed.TotalMilliseconds;
        var gin = await Lab.Median(() => Lab.Exec($"SELECT * FROM {s}.events WHERE tenant = $1 AND subjects @> ARRAY[$2]", Lab.TenantA, sub), 25);
        Console.WriteLine($"| subjects @> ARRAY[s], GIN index on subjects | {Lab.F(gin)} ms | {await Lab.Scalar<string>($"SELECT pg_size_pretty(pg_relation_size('{s}.events_subjects'))")} | {Lab.F(build)} ms |");
        var side = await Lab.Median(() => Lab.Exec($"SELECT e.* FROM {s}.subject_streams x JOIN {s}.events e ON (e.tenant, e.type, e.stream_id) = (x.tenant, x.type, x.stream_id) WHERE x.tenant = $1 AND x.subject = $2 AND e.subjects @> ARRAY[$2]", Lab.TenantA, sub), 25);
        Console.WriteLine($"| a subject_streams table (subject → streams), then the streams' events | {Lab.F(side)} ms | {await Lab.Scalar<string>($"SELECT pg_size_pretty(pg_total_relation_size('{s}.subject_streams'))")} (table + key) | – |");
    }

    // ── (f) forgetting in a projection: rebuild everything vs re-fold the subject's streams
    private static async Task Forget(int streams)
    {
        var codec = Lab.Codec();
        var store = await Lab.Store("dpf", fresh: true);
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) is var i && i < streams)
            {
                var c = Customer.Register(Lab.Time.GetUtcNow(), $"Customer {i}", $"c{i}@example.org", "DE");
                for (var k = 0; k < 4; k++) _ = c.Promote();
                await Lab.Commit(store, c, 0, Lab.Context(Lab.TenantA));
            }
        })));
        Lab.Info($"{Lab.N(streams)} customers × 5 events in today's store; projection customer-list (personal columns name, email)");
        await Lab.Exec("DROP SCHEMA IF EXISTS dpf_rm CASCADE");
        var rm = new NpgsqlReadModels(Lab.Db, Lab.Json, "dpf_rm");
        var host = new ProjectionHost<CustomerRow>(store, rm.Store<CustomerRow>(), new ProjectionOptions { Batch = 1000 });
        var sw = Stopwatch.StartNew();
        while (await host.RunOnce() > 0) { }
        var rebuild = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine("| forget one subject in the projection | time |"); Console.WriteLine("|---|---|");
        Console.WriteLine($"| rebuild the projection from the feed ({Lab.N(streams * 5)} events, today's ProjectionHost, batch 1,000) | {Lab.F(rebuild)} ms |");
        var v = new Vault(Lab.Db, codec, "dp", new KeyCache(TimeSpan.FromMinutes(5)));
        await Lab.Exec(v.Script());
        var subject = Customer.Register(Lab.Time.GetUtcNow(), "Alice", "alice@example.org", "DE");
        for (var k = 0; k < 4; k++) _ = subject.Promote();
        await v.Append(Lab.TenantA, subject, 0);
        await Lab.Exec("DROP TABLE IF EXISTS dp.rm_customers; CREATE TABLE dp.rm_customers (tenant uuid, id uuid, version bigint, name text, email text, country text, doc jsonb, PRIMARY KEY (tenant, id))");
        await v.Erase(Lab.TenantA, subject.Id.Value);
        var refold = await Lab.Median(async () =>
        {
            await using var c = Lab.Db.CreateCommand("SELECT DISTINCT stream_id FROM dp.events WHERE tenant = $1 AND type = 'customer' AND subjects @> ARRAY[$2]");
            c.Parameters.Add(new() { Value = Lab.TenantA }); c.Parameters.Add(new() { Value = subject.Id.Value });
            var list = new List<Guid>();
            await using (var r = await c.ExecuteReaderAsync()) while (await r.ReadAsync()) list.Add(r.GetGuid(0));
            foreach (var id in list) await SelfTest.Refold(v, Lab.TenantA, id);
        });
        Console.WriteLine($"| re-fold the subject's streams (find them by subjects, load with the key gone, replace the row) | {Lab.F(refold)} ms |");
        var del = await Lab.Median(() => Lab.Exec("DELETE FROM dp.rm_customers WHERE tenant = $1 AND id = $2", Lab.TenantA, subject.Id.Value));
        Console.WriteLine($"| delete the subject's row (a projection that forgets by deleting) | {Lab.F(del)} ms |");
    }

    // ── (g) retention by deleting whole streams of a type, in batches, while writers commit
    private static async Task Retention(int streams)
    {
        var store = await Lab.Store("dpr", fresh: true);
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) is var i && i < streams)
            {
                var c = Customer.Register(Lab.Time.GetUtcNow(), $"Customer {i}", $"c{i}@example.org", "DE");
                for (var k = 0; k < 4; k++) _ = c.Promote();
                await Lab.Commit(store, c, 0, Lab.Context(Lab.TenantA));
            }
        })));
        await Lab.Exec("ANALYZE dpr.events; ANALYZE dpr.streams");
        Lab.Info($"{Lab.N(streams)} customers × 5 events ({await Lab.Scalar<string>("SELECT pg_size_pretty(pg_total_relation_size('dpr.events'))")}); half of them past retention (updated_at moved back 400 days)");
        await Lab.Exec($"UPDATE dpr.streams SET updated_at = updated_at - interval '400 days' WHERE ('x' || substr(id::text, 25, 4))::bit(16)::int % 2 = 0");
        Console.WriteLine("| run | one batch, median | one batch, max | other writers' commits p50 / p99 / max |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var batch in new[] { 0, 500, 5000 })
        {
            var t = new List<double>(); var stop = false; var deleted = 0L; var batches = 0; var batchMs = new List<double>();
            var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
            {
                var mine = new List<double>();
                while (!Volatile.Read(ref stop))
                {
                    var c = Customer.Register(Lab.Time.GetUtcNow(), "W", "w@example.org", "DE");
                    var sw = Stopwatch.StartNew(); await Lab.Commit(store, c, 0, Lab.Context(Lab.TenantA)); mine.Add(sw.Elapsed.TotalMilliseconds);
                }
                lock (t) t.AddRange(mine);
            })).ToArray();
            await Task.Delay(1500);
            var total = Stopwatch.StartNew();
            if (batch > 0)
                while (true)
                {
                    var sw = Stopwatch.StartNew();
                    var n = await Lab.Scalar<long>($"""
                        WITH s AS (SELECT tenant, type, id FROM dpr.streams WHERE type = 'customer' AND updated_at < now() - interval '365 days' LIMIT {batch} FOR UPDATE SKIP LOCKED),
                             e AS (DELETE FROM dpr.events x USING s WHERE (x.tenant, x.type, x.stream_id) = (s.tenant, s.type, s.id) RETURNING 1),
                             l AS (DELETE FROM dpr.lookups x USING s WHERE (x.tenant, x.type, x.stream_id) = (s.tenant, s.type, s.id) RETURNING 1),
                             d AS (DELETE FROM dpr.streams x USING s WHERE (x.tenant, x.type, x.id) = (s.tenant, s.type, s.id) RETURNING 1)
                        SELECT (SELECT count(*) FROM d) + 0 * (SELECT count(*) FROM e) + 0 * (SELECT count(*) FROM l)
                        """);
                    batchMs.Add(sw.Elapsed.TotalMilliseconds);
                    if (n == 0) break;
                    deleted += n; batches++;
                }
            else await Task.Delay(3000);
            var elapsed = total.Elapsed.TotalMilliseconds;
            await Task.Delay(500);
            Volatile.Write(ref stop, true); await Task.WhenAll(writers);
            t.Sort(); batchMs.Sort();
            Console.WriteLine(batch == 0
                ? $"| no deletion (baseline, 4 writers) | – | – | {Lab.F(Lab.Pct(t, 0.5))} / {Lab.F(Lab.Pct(t, 0.99))} / {Lab.F(t[^1])} ms |"
                : $"| delete {Lab.N(deleted)} streams in batches of {Lab.N(batch)} ({batches} batches, {Lab.F(elapsed)} ms) | {Lab.F(Lab.Pct(batchMs, 0.5))} ms | {Lab.F(batchMs[^1])} ms | {Lab.F(Lab.Pct(t, 0.5))} / {Lab.F(Lab.Pct(t, 0.99))} / {Lab.F(t[^1])} ms |");
            if (batch == 0) { }
            if (batch == 500)   // the next run needs streams to delete again: age the same number of the remaining ones
                await Lab.Exec("UPDATE dpr.streams SET updated_at = updated_at - interval '400 days' WHERE (tenant, type, id) IN (SELECT tenant, type, id FROM dpr.streams WHERE type = 'customer' AND updated_at > now() - interval '365 days' LIMIT $1)", deleted);
        }
    }
}
