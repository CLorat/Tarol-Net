using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql.IntegrationTests;

/// <summary>
/// Step 16B on PostgreSQL: the probe's scenarios (probes/data-protection/SelfTest.cs) against the real store, the key store's
/// cache and NOTIFY, the subject queries, retention deletes and the schema rules (data-protection.md §11).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataProtectionTests(Postgres postgres)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventCodec Codec() => new EventCodec(new JsonSerializerOptions(PrivacyJson.Default.Options)
    {
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(PrivacyJson.Default, Contract.ContractJson.Default),
    })
        .Add<Patron>().Add<Purchase>().Add<Contract.Member>().Add<Contract.Tally>()
        .Upcast<Patron>(new SignedUpToRegistered());

    private OperationContext NewTenant() => OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(_time), _time);

    private static Guid TenantOf(OperationContext context) => context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty;

    private async Task<(NpgsqlAggregateStore Store, NpgsqlKeyStore Keys)> Sealed(StoreLookups? lookups = null, IOutboxReactions? reactions = null)
    {
        var schema = postgres.NewSchema();
        var keys = postgres.KeyStore(_time, schema);
        var store = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions
        {
            Schema = schema,
            Lookups = lookups ?? new StoreLookups(),
            Reactions = reactions,
            Keys = keys,
        });
        await store.EnsureSchema(Ct);
        return (store, keys);
    }

    private static async Task Commit<T>(NpgsqlAggregateStore store, T aggregate, OperationContext context, long expected = 0)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        (await store.Append(aggregate, expected, context, Option.None, Ct)).ToResult().ShouldBe(Result.Success());
        AggregateChanges.Committed(aggregate);
    }

    private static async Task<T> Load<T>(NpgsqlAggregateStore store, Id<T> id, OperationContext context)
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => (await store.Load(id, context, Ct)).GetValueOrThrow();

    private async Task<(Patron Alice, Purchase Order)> Seed(NpgsqlAggregateStore store, OperationContext context)
    {
        var alice = Patron.Register(Id<Patron>.New(_time), "Alice Example", "alice@example.org", "1 Main Street", "Berlin", "DE");
        await Commit(store, alice, context);
        var order = Purchase.Place(Id<Purchase>.New(_time), alice.Id.Value, "Alice Example, 1 Main Street", 99.5m);
        await Commit(store, order, context);
        return (alice, order);
    }

    private async Task<string> Text(string sql) => (string)(await postgres.Scalar(sql))!;

    [Fact]
    public async Task An_Append_Seals_The_Personal_Fields_And_Records_Their_Subjects()
    {
        var (store, _) = await Sealed();
        var context = NewTenant();
        var (alice, order) = await Seed(store, context);

        var payload = await Text($"SELECT payload::text FROM {store.Schema}.events WHERE stream_id = '{alice.Id.Value}'");
        payload.ShouldNotContain("Alice Example");
        payload.ShouldNotContain("alice@example.org");
        payload.ShouldNotContain("Main Street");
        payload.ShouldContain("\"country\": \"DE\"");
        payload.ShouldContain("tdp1.");
        (await Text($"SELECT state->>'city' FROM {store.Schema}.streams WHERE id = '{alice.Id.Value}'")).ShouldBe("Berlin"); // plain fields stay queryable
        (await Text($"SELECT state::text FROM {store.Schema}.streams WHERE id = '{alice.Id.Value}'")).ShouldNotContain("alice@");
        (await Text($"SELECT subjects::text FROM {store.Schema}.events WHERE stream_id = '{order.Id.Value}'")).ShouldBe("{" + alice.Id.Value + "}");
        (await Text($"SELECT subjects::text FROM {store.Schema}.streams WHERE id = '{alice.Id.Value}'")).ShouldBe("{" + alice.Id.Value + "}");
    }

    [Fact]
    public async Task Another_Process_Opens_Every_Field_And_The_Order_Opens_With_The_Customers_Key()
    {
        var (store, _) = await Sealed();
        var context = NewTenant();
        var (alice, order) = await Seed(store, context);
        await using var otherKeys = postgres.KeyStore(_time, store.Schema);
        var other = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = store.Schema, Keys = otherKeys });

        var loaded = await Load(other, alice.Id, context);
        loaded.Name.ShouldBe("Alice Example");
        loaded.Email.ShouldBe("alice@example.org");
        loaded.Street.ShouldBe("1 Main Street");
        (await Load(other, order.Id, context)).ShipTo.ShouldBe("Alice Example, 1 Main Street");
        Patron.Rehydrate(alice.Id, [.. (await other.ReadStream(new StreamRef(TenantOf(context), "patron", alice.Id.Value), Ct)).Select(static e => e.Event.Event)])
            .Email.ShouldBe("alice@example.org");
    }

    [Fact]
    public async Task A_Cached_Key_Costs_No_Key_Query()
    {
        var (store, keys) = await Sealed();
        var context = NewTenant();
        var (alice, _) = await Seed(store, context);
        await Load(store, alice.Id, context);

        var before = keys.KeyQueries;
        for (var i = 0; i < 5; i++)
            (await Load(store, alice.Id, context)).Name.ShouldBe("Alice Example");
        keys.KeyQueries.ShouldBe(before);
    }

    [Fact]
    public async Task An_Erasure_Between_Reading_A_Key_And_Caching_It_Does_Not_Leave_The_Key_Cached()
    {
        var (_, keys) = await Sealed();
        var (tenant, subject) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        (await keys.Keys(tenant, [subject], create: true, Ct)).TryGet(subject, out var created).ShouldBeTrue();
        created!.IsErased.ShouldBeFalse();
        keys.ClearCache();
        keys.AfterKeyRead = async () =>
        {
            keys.AfterKeyRead = null;
            (await keys.EraseSubject(tenant, subject, Ct)).ShouldBeTrue();             // commits after the read, before the cache
        };

        _ = await keys.Keys(tenant, [subject], create: false, Ct);                       // read before the erasure: may still open

        (await keys.Keys(tenant, [subject], create: false, Ct)).TryGet(subject, out var after).ShouldBeTrue();
        after!.IsErased.ShouldBeTrue();                                                 // the old key was not kept in the cache
    }

    [Fact]
    public async Task An_Envelope_Relabelled_To_Another_Subject_Does_Not_Open()
    {
        var (store, _) = await Sealed();
        var context = NewTenant();
        var (alice, _) = await Seed(store, context);
        var bob = Patron.Register(Id<Patron>.New(_time), "Bob", "bob@example.org", "2 Side St", "Hamburg", "DE");
        await Commit(store, bob, context);

        // Alice's subject in front of Bob's nonce, tag and ciphertext: Alice's key opens nothing that was not sealed for her
        await postgres.Execute($"""
            UPDATE {store.Schema}.streams a SET state = jsonb_set(a.state, ARRAY['name'],
                (SELECT jsonb_build_object('v', 'tdp1.' || substr(a.state->>'name', 6, 22) || substr(b.state->>'name', 28)) -> 'v'
                 FROM {store.Schema}.streams b WHERE b.id = '{bob.Id.Value}'))
            WHERE a.id = '{alice.Id.Value}'
            """);
        await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(async () => await store.Load(alice.Id, context, Ct));
    }

    [Fact]
    public async Task Opening_Runs_Before_Upcasting()
    {
        var (store, keys) = await Sealed();
        var context = NewTenant();
        var carla = Patron.Register(Id<Patron>.New(_time), "Carla", "carla@example.org", "3 Old St", "Paris", "FR");
        await Commit(store, carla, context);
        // rewrite the stored event as the old "patron.signed_up" v1 with its sealed name under "fullName"
        await postgres.Execute($"""
            UPDATE {store.Schema}.events SET alias = 'patron.signed_up', payload = (payload - 'name') || jsonb_build_object('fullName', payload->'name')
            WHERE stream_id = '{carla.Id.Value}'
            """);
        await postgres.Execute($"UPDATE {store.Schema}.streams SET state = NULL WHERE id = '{carla.Id.Value}'");
        keys.ClearCache();

        var loaded = await Load(store, carla.Id, context);
        loaded.Name.ShouldBe("Carla");
        loaded.Email.ShouldBe("carla@example.org");
    }

    [Fact]
    public async Task The_Erasures_Notify_Evicts_The_Key_In_Another_Process()
    {
        var (store, keys) = await Sealed();
        var context = NewTenant();
        var (alice, _) = await Seed(store, context);
        await using var otherKeys = postgres.KeyStore(_time, store.Schema);
        await otherKeys.StartListening(Ct);
        var other = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = store.Schema, Keys = otherKeys });
        (await Load(other, alice.Id, context)).Name.ShouldBe("Alice Example"); // the other process caches the key
        otherKeys.CachedKeys.ShouldBe(1);
        var evicted = new TaskCompletionSource<KeyErasedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        otherKeys.Erased += (_, e) => evicted.TrySetResult(e);

        var sent = Stopwatch.StartNew();
        (await keys.EraseSubject(TenantOf(context), alice.Id.Value, Ct)).ShouldBeTrue();
        var signal = await evicted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // bounded by the notification, not a clock
        sent.Stop();

        signal.Subject.ShouldBe(alice.Id.Value);
        otherKeys.CachedKeys.ShouldBe(0);
        (await Load(other, alice.Id, context)).Name.ShouldBeNull();
        TestContext.Current.SendDiagnosticMessage($"NOTIFY eviction after {sent.Elapsed.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public async Task After_Erasure_The_Customer_Still_Loads_And_Others_Keep_Their_Data()
    {
        var (store, keys) = await Sealed();
        var context = NewTenant();
        var (alice, order) = await Seed(store, context);
        var bob = Patron.Register(Id<Patron>.New(_time), "Bob Sample", "bob@example.org", "2 Side St", "Hamburg", "DE");
        await Commit(store, bob, context);

        (await keys.EraseSubject(TenantOf(context), alice.Id.Value, Ct)).ShouldBeTrue();
        (await keys.EraseSubject(TenantOf(context), alice.Id.Value, Ct)).ShouldBeFalse();

        var erased = await Load(store, alice.Id, context);
        (erased.Name, erased.Email, erased.Street, erased.City, erased.Country).ShouldBe((null, null, null, "Berlin", "DE"));
        var replayed = Patron.Rehydrate(alice.Id, [.. (await store.ReadStream(new StreamRef(TenantOf(context), "patron", alice.Id.Value), Ct)).Select(static e => e.Event.Event)]);
        (replayed.Name, replayed.City).ShouldBe((null, "Berlin"));
        var placed = await Load(store, order.Id, context);
        (placed.ShipTo, placed.Total).ShouldBe((null, 99.5m));
        (await Load(store, bob.Id, context)).Email.ShouldBe("bob@example.org");
        (await Text($"SELECT (wrapped IS NULL AND erased_at IS NOT NULL)::text FROM {store.Schema}.subject_keys WHERE subject = '{alice.Id.Value}'")).ShouldBe("true");
    }

    [Fact]
    public async Task New_Personal_Data_For_An_Erased_Subject_Is_Refused()
    {
        var (store, keys) = await Sealed();
        var context = NewTenant();
        var (alice, _) = await Seed(store, context);
        await keys.EraseSubject(TenantOf(context), alice.Id.Value, Ct);

        var loaded = await Load(store, alice.Id, context);
        loaded.ChangeEmail("new@example.org");
        (await store.Append(loaded, 1, context, Option.None, Ct)).TryGetValue(out Violation? refused).ShouldBeTrue();
        refused.ShouldBeOfType<Error>().Code.ShouldBe(DataProtectionErrors.SubjectErasedCode);

        var order = Purchase.Place(Id<Purchase>.New(_time), alice.Id.Value, "elsewhere", 1m);
        (await store.Append(order, 0, context, Option.None, Ct)).TryGetValue(out Violation? orderRefused).ShouldBeTrue();
        orderRefused.ShouldBeOfType<Error>().Code.ShouldBe(DataProtectionErrors.SubjectErasedCode);

        var moved = await Load(store, alice.Id, context);
        moved.Relocate("Munich");
        await Commit(store, moved, context, 1);   // nothing personal: accepted
        (await Load(store, alice.Id, context)).City.ShouldBe("Munich");
    }

    [Fact]
    public async Task The_Subjects_Streams_And_Events_Are_Found_And_Opened_With_Its_Key_Only()
    {
        var (store, _) = await Sealed();
        var context = NewTenant();
        var tenant = TenantOf(context);
        var (alice, order) = await Seed(store, context);
        var bob = Patron.Register(Id<Patron>.New(_time), "Bob Sample", "bob@example.org", "2 Side St", "Hamburg", "DE");
        await Commit(store, bob, context);
        var bobsGift = Purchase.Place(Id<Purchase>.New(_time), bob.Id.Value, "Bob Sample, 2 Side St", 5m);
        await Commit(store, bobsGift, context);

        var streams = await store.StreamsOf(tenant, bob.Id.Value, Ct);
        streams.ShouldBe([new StreamRef(tenant, "patron", bob.Id.Value), new StreamRef(tenant, "purchase", bobsGift.Id.Value)], ignoreOrder: true);
        var events = await store.EventsOf(tenant, bob.Id.Value, Ct);
        events.Count.ShouldBe(2);
        events.Select(static e => e.Event.Event.Event).OfType<Patron.Registered>().Single().Name.ShouldBe("Bob Sample");
        events.Select(static e => e.Event.Event.Event).OfType<Purchase.Placed>().Single().ShipTo.ShouldBe("Bob Sample, 2 Side St");
        (await store.EventsOf(tenant, alice.Id.Value, Ct)).Select(static e => e.StreamType).ShouldBe(["patron", "purchase"]);
        Encoding.UTF8.GetString((await store.StateOf(new StreamRef(tenant, "patron", bob.Id.Value), bob.Id.Value, Ct))!).ShouldContain("bob@example.org");
        Encoding.UTF8.GetString((await store.StateOf(new StreamRef(tenant, "purchase", order.Id.Value), bob.Id.Value, Ct))!).ShouldNotContain("Alice"); // another subject: null
    }

    [Fact]
    public async Task A_Personal_Lookup_Keeps_A_Blind_Index()
    {
        var lookups = new StoreLookups().DefinePersonal<Patron>("email", static p => p.Email?.Trim().ToLowerInvariant());
        var (store, keys) = await Sealed(lookups);
        var context = NewTenant();
        var (alice, _) = await Seed(store, context);

        ((await store.Find<Patron>("email", "alice@example.org", context, Ct)).TryGetValue(out Id<Patron> found) ? found : default).ShouldBe(alice.Id);
        (await store.Find<Patron>("email", "alice@example.org", NewTenant(), Ct)).IsNone.ShouldBeTrue();
        (await Text($"SELECT key FROM {store.Schema}.lookups WHERE stream_id = '{alice.Id.Value}'")).ShouldNotContain("alice");
        var twin = Patron.Register(Id<Patron>.New(_time), "Twin", " Alice@Example.org", "x", "y", "DE");
        (await store.Append(twin, 0, context, Option.None, Ct)).TryGetValue(out Violation? taken).ShouldBeTrue();
        taken.ShouldBeOfType<Error>().Code.ShouldBe(StoreErrors.LookupTakenCode);

        (await store.DeletePersonalLookups(TenantOf(context), [new StreamRef(TenantOf(context), "patron", alice.Id.Value)], Ct)).ShouldBe(1);
        (await store.Find<Patron>("email", "alice@example.org", context, Ct)).IsNone.ShouldBeTrue();
        (await keys.BlindIndex(TenantOf(context), "email", "alice@example.org", false, Ct)).ShouldNotBe(await keys.BlindIndex(Guid.CreateVersion7(), "email", "alice@example.org", true, Ct));
    }

    [Fact]
    public async Task Erasing_The_Tenant_Key_Makes_Every_Subject_Of_The_Tenant_Unreadable()
    {
        var (store, keys) = await Sealed();
        var context = NewTenant();
        var (alice, _) = await Seed(store, context);
        var other = NewTenant();
        var (carla, _) = await Seed(store, other);

        (await keys.EraseTenant(TenantOf(context), Ct)).ShouldBeTrue();

        (await Load(store, alice.Id, context)).Name.ShouldBeNull();
        (await Load(store, carla.Id, other)).Name.ShouldBe("Alice Example");
        var refused = Patron.Register(Id<Patron>.New(_time), "New", "new@example.org", "x", "y", "DE");
        (await store.Append(refused, 0, context, Option.None, Ct)).TryGetValue(out Violation? violation).ShouldBeTrue();
        violation.ShouldBeOfType<Error>().Code.ShouldBe(DataProtectionErrors.SubjectErasedCode);
        (await Text($"SELECT (wrapped IS NULL)::text FROM {store.Schema}.tenant_keys WHERE tenant = '{TenantOf(context)}'")).ShouldBe("true");
    }

    [Fact]
    public async Task The_Feed_Opens_A_Page_With_One_Key_Query()
    {
        var (store, _) = await Sealed();
        var context = NewTenant();
        for (var i = 0; i < 50; i++)
            await Commit(store, Patron.Register(Id<Patron>.New(_time), $"Patron {i}", $"p{i}@example.org", "x", "y", "DE"), context);
        await using var cold = postgres.KeyStore(_time, store.Schema);
        var reader = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = store.Schema, Keys = cold });

        var events = new List<PositionedEvent>();
        var waited = Stopwatch.StartNew();
        while (events.Count < 50 && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            events.Clear();
            await foreach (var e in reader.ReadAll(GlobalPosition.Start, 1000, Ct))
                events.Add(e);
        }

        events.Count.ShouldBe(50);
        events.Select(static e => ((Patron.Registered)e.Event.Event.Event).Email).ShouldAllBe(static email => email!.EndsWith("@example.org", StringComparison.Ordinal));
        cold.KeyQueries.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(waited.Elapsed.TotalSeconds * 1000)); // sanity
        var before = cold.KeyQueries;
        cold.ClearCache();
        await foreach (var _ in reader.ReadAll(GlobalPosition.Start, 1000, Ct))
        {
        }

        (cold.KeyQueries - before).ShouldBe(1); // 50 subjects of one tenant: one query
    }

    [Fact]
    public async Task Delete_Streams_Removes_Expired_Streams_But_Skips_Pending_Reactions()
    {
        var reactions = OutboxReactions.From(static e => e is Purchase.Placed ? Reactions.Ship : ReadOnlySpan<string>.Empty);
        var (store, _) = await Sealed(new StoreLookups().DefinePersonal<Patron>("email", static p => p.Email), reactions);
        var context = NewTenant();
        var (alice, order) = await Seed(store, context);
        _time.Advance(TimeSpan.FromDays(31));
        var fresh = Patron.Register(Id<Patron>.New(_time), "Fresh", "fresh@example.org", "x", "y", "DE");
        await Commit(store, fresh, context);

        var deleted = await store.DeleteStreams("patron", _time.GetUtcNow().AddDays(-30), 500, Ct);
        deleted.ShouldBe([new StreamRef(TenantOf(context), "patron", alice.Id.Value)]);
        (await store.DeleteStreams("purchase", _time.GetUtcNow().AddDays(-30), 500, Ct)).ShouldBeEmpty(); // its reaction is pending

        (await store.Load(alice.Id, context, Ct)).IsNone.ShouldBeTrue();
        (await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.events WHERE stream_id = '{alice.Id.Value}'")).ShouldBe(0L);
        (await postgres.Scalar($"SELECT count(*) FROM {store.Schema}.lookups WHERE stream_id = '{alice.Id.Value}'")).ShouldBe(0L);
        (await Load(store, fresh.Id, context)).Name.ShouldBe("Fresh");
        (await Load(store, order.Id, context)).Total.ShouldBe(99.5m);
    }

    [Fact]
    public async Task Aggregates_Without_Personal_Data_Are_Stored_Byte_Identically()
    {
        var schema = postgres.NewSchema();
        var plain = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema });
        await plain.EnsureSchema(Ct);
        await using var keys = postgres.KeyStore(_time, schema);
        var sealing = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        var context = NewTenant();
        var a = Contract.Tally.Start(Id<Contract.Tally>.New(_time));
        a.Bump();
        var b = Contract.Tally.Start(Id<Contract.Tally>.New(_time));
        b.Bump();

        await Commit(plain, a, context);
        await Commit(sealing, b, context);

        var rows = await Text($"""
            SELECT string_agg(payload::text || '|' || coalesce(subjects::text, 'null'), ',' ORDER BY stream_id = '{a.Id.Value}', version)
            FROM {schema}.events WHERE type = 'tally'
            """);
        var halves = rows.Split(',');
        halves[..2].ShouldBe(halves[2..]);
        halves.ShouldAllBe(static h => h.EndsWith("|null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Sealing_Store_Raises_Compatible_From_So_Code_Before_Sealing_Refuses()
    {
        var schema = postgres.NewSchema();
        var plain = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema });
        (await plain.EnsureSchema(SchemaMode.Upgrade, Ct)).ShouldBe(new SchemaState(3, 1, 8));
        // older code (version 2) still runs on the version 3 schema of a store that does not seal
        await Version2Code(schema).Ensure(SchemaMode.VerifyOnly, Ct);

        await using var keys = postgres.KeyStore(_time, schema);
        var sealing = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        (await sealing.EnsureSchema(SchemaMode.Upgrade, Ct)).CompatibleFrom.ShouldBe(3);

        (await Should.ThrowAsync<InvalidOperationException>(() => Version2Code(schema).Ensure(SchemaMode.VerifyOnly, Ct))).Message.ShouldContain("compatible");
        (await plain.EnsureSchema(SchemaMode.VerifyOnly, Ct)).CompatibleFrom.ShouldBe(3); // this code still runs
    }

    private NpgsqlSchemaUpgrader Version2Code(string schema)
    {
        var definition = NpgsqlSchema.Definition(schema);
        return new NpgsqlSchemaUpgrader(postgres.DataSource,
            new NpgsqlSchemaDefinition(definition.Owner, schema, definition.VersionTable, definition.Baseline, [.. definition.Steps.Where(static s => s.Version <= 2)]));
    }

    [Fact]
    public async Task A_Version_2_Database_Upgrades_While_Writers_Commit()
    {
        var schema = postgres.NewSchema();
        await Version2Code(schema).Ensure(SchemaMode.Upgrade, Ct);
        await postgres.Execute(Seed2(schema, 2_000));
        var stop = new CancellationTokenSource();
        var errors = 0;
        var written = 0;
        // four "version 2 pods" keep inserting events the way version 2 code does (no subjects column)
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await postgres.Execute(Seed2(schema, 1));
                    Interlocked.Increment(ref written);
                }
                catch (PostgresException)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }, CancellationToken.None)).ToArray();
        while (Volatile.Read(ref written) < 20)
            await Task.Delay(10, Ct);

        var state = await new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema }).EnsureSchema(SchemaMode.Upgrade, Ct);
        var after = Volatile.Read(ref written);
        while (Volatile.Read(ref written) < after + 20)
            await Task.Delay(10, Ct);
        await stop.CancelAsync();
        await Task.WhenAll(writers);

        state.ShouldBe(new SchemaState(3, 1, 5));
        errors.ShouldBe(0);
        (await postgres.Scalar($"SELECT indisvalid FROM pg_index WHERE indexrelid = '{schema}.events_subjects'::regclass")).ShouldBe(true);
    }

    private static string Seed2(string schema, int count) => $"""
        INSERT INTO {schema}.events (tenant, type, stream_id, version, event_id, alias, alias_version, entity_id, payload, recorded_at, actor_kind, actor_id, correlation_id)
        SELECT gen_random_uuid(), 'tally', s, 1, gen_random_uuid(), 'tally.started', 1, s, jsonb_build_object(), now(), 'User', 'alice', gen_random_uuid()
        FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, {count})) g
        """;

    [Fact]
    public async Task Seal_History_Seals_Plaintext_Written_Before_The_Keys()
    {
        var schema = postgres.NewSchema();
        var plain = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema });
        await plain.EnsureSchema(Ct);
        var context = NewTenant();
        var (alice, order) = await Seed(plain, context);
        (await Text($"SELECT payload::text FROM {schema}.events WHERE stream_id = '{alice.Id.Value}'")).ShouldContain("alice@example.org");
        await using var keys = postgres.KeyStore(_time, schema);
        var sealing = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema, Keys = keys });

        (await sealing.SealHistory<Patron>(500, Ct)).ShouldBe((1L, 1L));
        (await sealing.SealHistory<Purchase>(500, Ct)).ShouldBe((1L, 1L));
        (await sealing.SealHistory<Patron>(500, Ct)).ShouldBe((0L, 0L)); // rerun: nothing left

        (await Text($"SELECT payload::text || state::text FROM {schema}.events e JOIN {schema}.streams s ON s.id = e.stream_id WHERE e.stream_id = '{alice.Id.Value}'"))
            .ShouldNotContain("alice@example.org");
        (await Load(sealing, alice.Id, context)).Email.ShouldBe("alice@example.org");
        await keys.EraseSubject(TenantOf(context), alice.Id.Value, Ct);
        (await Load(sealing, order.Id, context)).ShipTo.ShouldBeNull();
    }

    [Fact]
    public async Task Four_Writers_Seal_Concurrently()
    {
        var (store, _) = await Sealed();
        var context = NewTenant();
        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            var mine = new List<Id<Patron>>();
            for (var i = 0; i < 50; i++)
            {
                var p = Patron.Register(Id<Patron>.New(_time), $"W{w} P{i}", $"w{w}p{i}@example.org", "x", "y", "DE");
                await Commit(store, p, context);
                mine.Add(p.Id);
            }

            return mine;
        }, Ct)));

        foreach (var id in ids.SelectMany(static i => i))
            (await Load(store, id, context)).Email.ShouldNotBeNull().ShouldEndWith("@example.org");
    }

    [Fact]
    public async Task The_Key_Cache_Stays_Bounded_While_Four_Writers_Fill_It()
    {
        // found by the SealHistory benchmark: the bound's eviction enumerated the cache while other threads added to it
        var schema = postgres.NewSchema();
        await using var keys = postgres.KeyStore(_time, schema, new NpgsqlKeyStoreOptions { MaxCachedKeys = 50 });
        var store = new NpgsqlAggregateStore(postgres.DataSource, Codec(), _time, new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        await store.EnsureSchema(Ct);
        var context = NewTenant();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                await Commit(store, Patron.Register(Id<Patron>.New(_time), $"W{w} {i}", $"w{w}.{i}@example.org", "x", "y", "DE"), context);
        }, Ct)));

        keys.CachedKeys.ShouldBeLessThanOrEqualTo(50 + 4);
    }

    private static class Reactions
    {
        public static readonly string[] Ship = ["Ship"];
    }
}
