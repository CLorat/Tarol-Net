using System.Text.Json;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.Npgsql;
using Tarol.Persistence.Npgsql.AotSmoke;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var codec = new EventCodec(new JsonSerializerOptions(SmokeJson.Default.Options)).Add<Wallet>();
var lookups = new StoreLookups().Define<Wallet>("owner", w => w.Owner);
Check("schema script", NpgsqlSchema.Script("smoke").Contains("CREATE TABLE IF NOT EXISTS smoke.events", StringComparison.Ordinal));
Check("upgrade script (step 15)", NpgsqlSchema.UpgradeScript("smoke", 1).Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS outbox_dead_at", StringComparison.Ordinal));

// The database checks need a PostgreSQL: TAROL_PG=<connection string> (the CI aot-smoke job provides one).
var connectionString = Environment.GetEnvironmentVariable("TAROL_PG");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("skip database checks: TAROL_PG is not set");
}
else
{
    await using var db = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    var schema = $"aot_smoke_{Environment.ProcessId}";
    var store = new NpgsqlAggregateStore(db, codec, TimeProvider.System, new NpgsqlStoreOptions
    {
        Schema = schema,
        Lookups = lookups,
        Reactions = OutboxReactions.From(e => e is Wallet.Opened ? Reactions.OnOpened : ReadOnlySpan<string>.Empty),
    });
    try
    {
        await store.EnsureSchema(default);
        Check("schema upgraded to the current version", await store.EnsureSchema(SchemaMode.VerifyOnly, default) == new SchemaState(NpgsqlSchema.Version, 1, 0));
        var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
        var key = new IdempotencyKey(context.Tenant, "smoke", "k-1");

        var wallet = Wallet.Open("alice");
        _ = wallet.Add(5);
        Check("append", (await store.Append(wallet, 0, context, key, default)).IsSuccess);
        AggregateChanges.Committed(wallet);
        var loaded = (await store.Load(wallet.Id, context, default)).GetValueOrThrow();
        Check("load from stored state", loaded.Total == 5 && loaded.Version == 2);
        Check("key stored", (await store.Seen(key, default)).GetValueOrThrow() == new StoredOutcome(wallet.Id.Value, 2, 2));
        Check("lookup", (await store.Find<Wallet>("owner", "alice", context, default)).GetValueOrThrow() == wallet.Id);
        var rival = (await store.Load(wallet.Id, context, default)).GetValueOrThrow();
        _ = loaded.Add(1);
        _ = rival.Add(2);
        Check("first writer commits", (await store.Append(loaded, 2, context, Option.None, default)).IsSuccess);
        Check("second writer: version conflict", StoreErrors.IsVersionConflict(await store.Append(rival, 2, context, Option.None, default)));
        var feed = new List<PositionedEvent>();
        await foreach (var e in store.ReadAll(GlobalPosition.Start, 10, default))
            feed.Add(e);
        Check("feed with context", feed.Count == 3 && feed.All(e => e.Event.Context == context && e.StreamType == "wallet"));
        Check("rebuild states", await store.RebuildStates<Wallet>(default) == 0);
        var claimed = await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), default);
        Check("outbox row claimed with its event", claimed.Count == 1 && claimed[0].Reaction == "Welcome" && claimed[0].Event.Event.Event.Event is Wallet.Opened);
        Check("outbox row completed", await store.Outbox.Complete(claimed[0], default) && await store.Outbox.Pending(default) == 0);
        var second = Wallet.Open("bob");
        Check("second commit", (await store.Append(second, 0, context, Option.None, default)).IsSuccess);
        var poison = (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), default)).Single();
        Check("bury moves the row to the dead letters", await store.Outbox.Bury(poison, "Conflict Wallet.Poison", default)
                                                      && (await store.Outbox.DeadLetters(10, default)) is [{ Reaction: "Welcome", LastError: { IsSome: true } }]);
        Check("re-drive brings it back", await store.Outbox.Redrive(poison.Id, default)
                                          && (await store.Outbox.Claim(10, TimeSpan.FromMinutes(1), default)) is [{ Attempts: 1 } again]
                                          && await store.Outbox.Complete(again, default));
        Check("purge keys", await store.PurgeKeys(TimeProvider.System.GetUtcNow().AddDays(1), 100, default) == 1);
        var due = DateTimeOffset.FromUnixTimeSeconds(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() - 1);
        Check("job rows sync", (await store.Jobs.Sync([new JobSchedule("Nightly", Option.None, "0 2 * * * UTC", true, due)], default)) is [{ Kind: JobSyncKind.Added }]);
        Check("job row claimed once (SKIP LOCKED)", (await store.Jobs.Claim(TimeSpan.FromMinutes(1), default)).TryGetValue(out JobRow? job)
                                                    && job.NextAt == due && (await store.Jobs.Claim(TimeSpan.FromMinutes(1), default)).TryGetValue(out None _));
        Check("job row bury and re-drive", await store.Jobs.Bury(job!, due.AddDays(1), "smoke", default)
                                          && (await store.Jobs.DeadJobs(10, default)) is [{ Occurrence: var dead } deadJob] && dead == due
                                          && await store.Jobs.Redrive(deadJob.Id, default)
                                          && (await store.Jobs.Claim(TimeSpan.FromMinutes(1), default)).TryGetValue(out JobRow? redrive)
                                          && redrive.Occurrence == due && await store.Jobs.Advance(redrive, redrive.NextAt, default)
                                          && (await store.Jobs.DeadJobs(10, default)).Count == 0);

        // step 16B: a sealing store with the key tables, natively
        await using var keys = new NpgsqlKeyStore(db, new ConfiguredKeyEncryptionKey("smoke", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            TimeProvider.System, new NpgsqlKeyStoreOptions { Schema = schema });
        var sealing = new NpgsqlAggregateStore(db, new EventCodec(new JsonSerializerOptions(PatientJson.Default.Options)).Add<Patient>(), TimeProvider.System,
            new NpgsqlStoreOptions { Schema = schema, Keys = keys });
        Check("a sealing store raises compatible_from", (await sealing.EnsureSchema(SchemaMode.Upgrade, default)).CompatibleFrom == NpgsqlSchema.SealingVersion);
        var patient = Patient.Admit("Ada", "B2");
        Check("append sealed", (await sealing.Append(patient, 0, context, Option.None, default)).IsSuccess);
        await using (var raw = db.CreateCommand($"SELECT payload::text FROM {schema}.events WHERE stream_id = '{patient.Id.Value}'"))
            // the plaintext would be the JSON string "Ada"; the sealed value is "tdp1.<base64url>", whose random characters can
            // spell Ada but never contain a quote
            Check("no plaintext stored", await raw.ExecuteScalarAsync() is string payload && !payload.Contains("\"Ada\"", StringComparison.Ordinal) && payload.Contains("\"tdp1.", StringComparison.Ordinal));
        Check("load opens", (await sealing.Load(patient.Id, context, default)).GetValueOrThrow().Name == "Ada");
        Check("erase", await keys.EraseSubject(context.Tenant.GetValueOrThrow().Value, patient.Id.Value, default));
        var erased = (await sealing.Load(patient.Id, context, default)).GetValueOrThrow();
        Check("erased reads null, the rest stays", erased.Name is null && erased.Ward == "B2");
    }
    finally
    {
        await using var drop = db.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

internal static class Reactions
{
    public static readonly string[] OnOpened = ["Welcome"];
}
