using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Queries;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Probe.DataProtection;

internal static class SelfTest
{
    public static async Task<int> Run(string kind) => kind switch
    {
        "protect" => await Protect(),
        _ => 2,
    };

    /// <summary>An upcaster from an old alias whose personal field had another name: it must see the plaintext.</summary>
    private sealed class RenamedField(string alias) : IEventUpcaster
    {
        public EventType From => new("customer.signed-up", 1);
        public EventType Into => new(alias, 1);
        public void Upcast(JsonObject payload) { payload["name"] = payload["fullName"]?.DeepClone(); payload.Remove("fullName"); }
    }

    private static async Task<int> Protect()
    {
        var registered = Customer.EventTypes.Events.Single(e => e.ClrType == typeof(Customer.Registered)).Type;
        var codec = Lab.Codec().Upcast<Customer>(new RenamedField(registered.Alias));
        var a = new Vault(Lab.Db, codec, "dp", new KeyCache(TimeSpan.FromMinutes(5)));
        await Lab.Exec(a.Script());
        var b = new Vault(Lab.Db, codec, "dp", new KeyCache(TimeSpan.FromMinutes(5)));   // a second process: its own caches
        var now = Lab.Time.GetUtcNow();
        var t = Lab.TenantA;

        // (1) append: personal fields leave the process sealed; the row records its subjects
        var alice = Customer.Register(now, "Alice Example", "alice@example.org", "DE");
        _ = alice.Move("1 Main Street", "Berlin");
        await a.Append(t, alice, 0);
        var stored = await Lab.Scalar<string>("SELECT string_agg(payload::text, ' ') FROM dp.events WHERE stream_id = $1", alice.Id.Value);
        Lab.Check(!stored.Contains("Alice Example") && !stored.Contains("alice@example.org") && !stored.Contains("Main Street") && stored.Contains("\"country\": \"DE\"") && stored.Contains("tdp1."),
            "stored payloads hold envelopes for name, email, street; country and city stay plain JSON", stored.Length > 160 ? stored[..160] + "…" : stored);
        var subjects = await Lab.Scalar<string>("SELECT string_agg(subjects::text, ' ') FROM dp.events WHERE stream_id = $1", alice.Id.Value);
        Lab.Check(subjects.Contains(alice.Id.Value.ToString()), "every event row records the subjects it holds data of", subjects);
        var order = Order.Place(now, alice.Id.Value, "Alice Example, 1 Main Street", 99.5m);
        await a.Append(t, order, 0);
        var bob = Customer.Register(now, "Bob Sample", "bob@example.org", "AT");
        await a.Append(t, bob, 0);

        // (2) load in another process: one round trip for events and keys
        var (la, lost) = await b.Load<Customer>(t, alice.Id.Value);
        Lab.Check(la.Name == "Alice Example" && la.Email == "alice@example.org" && la.Street == "1 Main Street" && lost == 0, "a load opens every field (keys fetched with the events in one batch)", $"{la.Name}, {la.Email}, {la.Street}");
        var (lo, _) = await b.Load<Order>(t, order.Id.Value);
        Lab.Check(lo.ShipTo == "Alice Example, 1 Main Street" && lo.Total == 99.5m, "another aggregate's field sealed for the customer opens with the customer's key", lo.ShipTo ?? "null");

        // (3) an envelope moved to another subject does not open (the subject is inside the envelope and in the associated data)
        var env = await Lab.Scalar<string>("SELECT payload->>'email' FROM dp.events WHERE stream_id = $1 AND version = 1", alice.Id.Value);
        var raw = Base64Url.DecodeFromChars(env.AsSpan(5));
        bob.Id.Value.TryWriteBytes(raw.AsSpan(0, 16));
        var forged = "tdp1." + Base64Url.EncodeToString(raw);
        var ring = new KeyRing();
        foreach (var s in new[] { alice.Id.Value, bob.Id.Value }) { _ = b.Cache.TryGet(t, s, out var k); ring.Add(s, k); }
        var plain = new ArrayBufferWriter<byte>();
        var opened = Envelope.Open(ring, System.Text.Encoding.UTF8.GetBytes(forged), plain, out _);
        Lab.Check(!opened, "an envelope relabelled to another subject fails authentication (AES-GCM tag)");

        // (4) an upcaster runs after the envelopes are opened: it sees plaintext under the old name
        var oldPayload = "{\"fullName\":\"Carla Old\",\"email\":\"carla@example.org\",\"country\":\"IT\"}"u8.ToArray();
        var carlaId = Id<Customer>.New(now).Value;
        var sealedOld = new ArrayBufferWriter<byte>();
        var carlaRing = await KeysFor(a, t, carlaId);
        FieldTransform.Seal(oldPayload, [new("fullName", carlaId), new("email", carlaId)], s => carlaRing.TryGet(s, out var k) ? k! : throw new InvalidOperationException(), sealedOld);
        await Lab.Exec("INSERT INTO dp.streams VALUES ($1, 'customer', $2, 1, NULL, NULL)", t, carlaId);
        await Lab.Exec($"INSERT INTO dp.events (tenant, type, stream_id, version, alias, alias_version, payload, subjects, recorded_at) VALUES ($1, $2, $3, 1, 'customer.signed-up', 1, $4::jsonb, ARRAY[$3], now())",
            t, codec.StreamTypeOf<Customer>(), carlaId, System.Text.Encoding.UTF8.GetString(sealedOld.WrittenSpan));
        var (carla, _) = await b.Load<Customer>(t, carlaId);
        Lab.Check(carla.Name == "Carla Old" && carla.Email == "carla@example.org", "opening runs before upcasting: an upcaster renames a sealed field as plaintext", carla.Name ?? "null");

        // (5) erasure: a tombstone and a NOTIFY; the other process's cached key is evicted by the notification
        await using var listen = await Lab.Db.OpenConnectionAsync();
        var evicted = new TaskCompletionSource<double>();
        var sw = new Stopwatch();
        listen.Notification += (_, e) =>
        {
            var parts = e.Payload.Split(':');
            b.Cache.Evict(Guid.Parse(parts[0]), Guid.Parse(parts[1]));
            evicted.TrySetResult(sw.Elapsed.TotalMilliseconds);
        };
        await using (var l = new NpgsqlCommand($"LISTEN {Vault.Channel}", listen)) await l.ExecuteNonQueryAsync();
        var waiting = Task.Run(async () => { while (!evicted.Task.IsCompleted) await listen.WaitAsync(TimeSpan.FromSeconds(5)); });
        _ = b.Cache.TryGet(t, alice.Id.Value, out var before);
        sw.Start();
        await a.Erase(t, alice.Id.Value);
        var latency = await evicted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await waiting;
        Lab.Check(before is not null && !b.Cache.TryGet(t, alice.Id.Value, out _), "the erasure's NOTIFY evicts the key from the other process's cache", $"{Lab.F(latency)} ms after the erase was sent");
        var (ea, elost) = await b.Load<Customer>(t, alice.Id.Value);
        Lab.Check(ea.Name is null && ea.Email is null && ea.Street is null && ea.Country == "DE" && ea.City == "Berlin" && elost == 3,
            "after erasure the aggregate still replays: personal fields read as null, the rest is intact", $"{elost} fields lost; country {ea.Country}, city {ea.City}");
        var (eo, _) = await b.Load<Order>(t, order.Id.Value);
        Lab.Check(eo.ShipTo is null && eo.Total == 99.5m, "the customer's data in an order is gone too; the order's own data stays", $"total {eo.Total}");
        var again = Customer.Register(now, "x", "y", "DE");
        string? refused = null;
        try { _ = alice; var revived = Customer.Rehydrate(alice.Id, (await b.Read(t, "customer", alice.Id.Value)).Events); _ = revived.Move("2 New Road", "Hamburg"); await b.Append(t, revived, 2); }
        catch (SubjectErasedException e) { refused = e.Message; }
        Lab.Check(refused is not null, "new personal data for an erased subject is refused (a tombstone, not a fresh key)", refused ?? "accepted");
        var (lb, blost) = await b.Load<Customer>(t, bob.Id.Value);
        Lab.Check(lb.Email == "bob@example.org" && blost == 0, "other subjects are unaffected");
        var key = await Lab.Scalar<long>("SELECT count(*) FROM dp.subject_keys WHERE subject = $1 AND wrapped IS NULL AND erased_at IS NOT NULL", alice.Id.Value);
        Lab.Check(key == 1, "the tombstone keeps when, not what: the key row has no key");

        // (6) export: every event of the subject, found by the subjects column, opened with the subject's key only
        var bobOrder = Order.Place(now, bob.Id.Value, "Bob Sample, Ring 2", 10m);
        await a.Append(t, bobOrder, 0);
        var export = await a.Export(t, bob.Id.Value);
        Lab.Check(export.Count == 2 && export.All(e => e.Json.Contains("Bob Sample")) && export.Any(e => e.Type == codec.StreamTypeOf<Order>()),
            "export lists the subject's events in every stream type (its customer stream and the order), opened", string.Join("; ", export.Select(e => $"{e.Type} v{e.Version}")));

        // (7) blind index for a personal lookup: equal for equal (normalized) values, different per tenant
        var k1 = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var k2 = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Lab.Check(BlindIndex.Of(k1, " Bob@Example.org") == BlindIndex.Of(k1, "bob@example.org") && BlindIndex.Of(k1, "bob@example.org") != BlindIndex.Of(k2, "bob@example.org"),
            "a blind index (HMAC-SHA256 under a tenant key) finds equal emails and differs between tenants", BlindIndex.Of(k1, "bob@example.org"));

        // (8) a tenant's key: deleting it shreds every subject of the tenant at once
        var carol = Customer.Register(now, "Carol Tenant", "carol@example.org", "FR");
        await a.Append(Lab.TenantB, carol, 0);
        await a.EraseTenant(Lab.TenantB);
        var fresh = new Vault(Lab.Db, codec, "dp", new KeyCache(TimeSpan.FromMinutes(5)));
        var (lc, clost) = await fresh.Load<Customer>(Lab.TenantB, carol.Id.Value);
        Lab.Check(lc.Name is null && lc.Email is null && clost == 2 && lc.Country == "FR", "erasing the tenant key makes all of the tenant's subjects unreadable (one row)");

        // (9) a projection that copied personal data forgets by re-folding the subject's streams (targeted, not a rebuild)
        await Lab.Exec("DROP TABLE IF EXISTS dp.rm_customers; CREATE TABLE dp.rm_customers (tenant uuid, id uuid, version bigint, name text, email text, country text, doc jsonb, PRIMARY KEY (tenant, id))");
        foreach (var c in new[] { alice.Id.Value, bob.Id.Value })
            await Refold(fresh, t, c);
        await Lab.Exec("UPDATE dp.rm_customers SET name = 'Alice Example', email = 'alice@example.org', doc = jsonb_set(doc, '{name}', '\"Alice Example\"') WHERE id = $1", alice.Id.Value);   // as written before the erasure
        var affected = await Lab.Scalar<long>("SELECT count(DISTINCT stream_id) FROM dp.events WHERE tenant = $1 AND type = 'customer' AND subjects @> ARRAY[$2]", t, alice.Id.Value);
        await Refold(fresh, t, alice.Id.Value);
        var row = await Lab.Scalar<string>("SELECT coalesce(name, 'null') || ' | ' || coalesce(email, 'null') || ' | ' || country || ' | ' || doc::text FROM dp.rm_customers WHERE id = $1", alice.Id.Value);
        Lab.Check(row.StartsWith("null | null | DE", StringComparison.Ordinal) && !row.Contains("alice@", StringComparison.Ordinal), $"re-folding the {affected} affected stream(s) nulls the row's personal columns and its doc", row);

        // (10) one AesGcm instance shared by threads without a lock: concurrent use corrupts results
        var shared = new System.Security.Cryptography.AesGcm(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 16);
        var failures = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            Span<byte> nonce = stackalloc byte[12], tag = stackalloc byte[16], ct = stackalloc byte[64], back = stackalloc byte[64];
            var msg = new byte[64]; msg[0] = (byte)w;
            for (var i = 0; i < 50_000; i++)
            {
                System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
                try { shared.Encrypt(nonce, msg, ct, tag); shared.Decrypt(nonce, ct, tag, back); if (back[0] != (byte)w) Interlocked.Increment(ref failures); }
                catch (System.Security.Cryptography.CryptographicException) { Interlocked.Increment(ref failures); }
            }
        })));
        Lab.Check(failures > 0, "one AesGcm instance used by 4 threads without a lock fails (so a cached key needs a lock or one instance per use)", $"{failures} of 200,000 round trips failed");

        _ = again;
        return Lab.Summary("protect");
    }

    private static async Task<KeyRing> KeysFor(Vault v, Guid tenant, Guid subject)
    {
        await using var c = await Lab.Db.OpenConnectionAsync();
        return await v.Keys(c, null, tenant, [subject], create: true);
    }

    private static T? Fold<T>(T? row, RecordedEvent e) where T : class, IProjection<T> => T.Apply(row, e);

    /// <summary>Folds one stream through the projection and replaces its row (the forced write of a re-fold).</summary>
    public static async Task Refold(Vault v, Guid tenant, Guid stream)
    {
        var (events, _) = await v.Read(tenant, "customer", stream);
        CustomerRow? row = null;
        var context = new OperationContext(Actor.System("Probe"), (Id<ITenant>)tenant, Id<ICorrelation>.New(Lab.Time.GetUtcNow()), Option.None);
        foreach (var e in events)
            row = Fold<CustomerRow>(row, new RecordedEvent(stream, e, Guid.NewGuid(), Lab.Time.GetUtcNow(), context));
        if (row is null) return;
        await Lab.Exec("""
            INSERT INTO dp.rm_customers VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)
            ON CONFLICT (tenant, id) DO UPDATE SET version = EXCLUDED.version, name = EXCLUDED.name, email = EXCLUDED.email, country = EXCLUDED.country, doc = EXCLUDED.doc
            """, tenant, stream, events[^1].Version, (object?)row.Name ?? DBNull.Value, (object?)row.Email ?? DBNull.Value, row.Country,
            System.Text.Json.JsonSerializer.Serialize(row, ProbeJson.Default.CustomerRow));
    }
}
