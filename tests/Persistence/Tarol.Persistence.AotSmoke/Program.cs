using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.AotSmoke;
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

var store = new InMemoryAggregateStore(TimeProvider.System, SmokeJson.Default.Options).DefineLookup<Wallet>("owner", w => w.Owner);
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
Check("recorded with context", store.Recorded.All(r => r.Context == context));

// step 8A: the codec round trip, an upcaster and the feed, natively
var codec = new EventCodec(new System.Text.Json.JsonSerializerOptions(SmokeJson.Default.Options)).Add<Wallet>().Upcast<Wallet>(new AddedV1ToV2());
var coded = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions { Codec = codec });
var second = Wallet.Open("bob");
_ = second.Add(3);
Check("append through the codec", (await coded.Append(second, 0, context, Option.None, default)).IsSuccess);
Check("load through the codec", (await coded.Load(second.Id, context, default)).GetValueOrThrow().Total == 3);
Check("stream type and catalog", Wallet.StreamType == "wallet" && Wallet.EventTypes.Events.Count == 2);
Check("upcast v1", codec.Decode("wallet", new EventType("wallet.added", 1), """{"value":4}"""u8) is Wallet.Added { Amount: 4 });
var feed = new List<PositionedEvent>();
await foreach (var e in coded.ReadAll(GlobalPosition.Start, 10, default))
    feed.Add(e);
Check("feed", feed.Count == 2 && feed[0].StreamType == "wallet" && feed[0].Position < feed[1].Position);

// step 9B: the in-memory outbox, natively
var withOutbox = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions
{
    Reactions = OutboxReactions.From(e => e is Wallet.Opened ? OutboxNames.OnOpened : ReadOnlySpan<string>.Empty),
});
var third = Wallet.Open("carol");
Check("append writes an outbox row", (await withOutbox.Append(third, 0, context, Option.None, default)).IsSuccess
                                    && await withOutbox.Outbox.Pending(default) == 1);
var row = (await withOutbox.Outbox.Claim(10, TimeSpan.FromMinutes(1), default)).Single();
Check("claim, bury, re-drive, complete", row.Reaction == "Welcome" && await withOutbox.Outbox.Bury(row, "x", default)
                                        && await withOutbox.Outbox.Redrive(row.Id, default)
                                        && (await withOutbox.Outbox.Claim(10, TimeSpan.FromMinutes(1), default)) is [var redriven]
                                        && await withOutbox.Outbox.Complete(redriven, default) && await withOutbox.Outbox.Pending(default) == 0);

var due = new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero);
var jobs = store.Jobs;
Check("job rows sync", (await jobs.Sync([new JobSchedule("Nightly", Option.None, "0 2 * * * UTC", true, due)], default)) is [{ Kind: JobSyncKind.Added }]);
Check("job row claimed once", (await jobs.Claim(TimeSpan.FromMinutes(1), default)).TryGetValue(out JobRow? job) && job.Occurrence == due
                              && (await jobs.Claim(TimeSpan.FromMinutes(1), default)).TryGetValue(out None _));
Check("job row advanced with its claim", await jobs.Advance(job!, due.AddDays(1), default) && (await jobs.Rows(default))[0].NextAt == due.AddDays(1));

// step 16A: sealed personal data, the key store and erasure, natively
var keyStore = InMemoryKeyStore.Create();
var sealing = new InMemoryAggregateStore(TimeProvider.System, new InMemoryStoreOptions
{
    Codec = new EventCodec(new System.Text.Json.JsonSerializerOptions(PatientJson.Default.Options)).Add<Patient>(),
    Keys = keyStore,
});
var patient = Patient.Admit("Ada", "B2");
Check("append sealed", (await sealing.Append(patient, 0, context, Option.None, default)).IsSuccess);
Check("load opens", (await sealing.Load(patient.Id, context, default)).GetValueOrThrow().Name == "Ada");
var tenantId = context.Tenant.GetValueOrThrow().Value;
Check("erase", await keyStore.EraseSubject(tenantId, patient.Id.Value, default));
var erased = (await sealing.Load(patient.Id, context, default)).GetValueOrThrow();
Check("erased reads null, the rest stays", erased.Name is null && erased.Ward == "B2");
_ = erased.Rename("Ada again");
Check("new data for the erased subject is refused",
    (await sealing.Append(erased, erased.Version - 1, context, Option.None, default)).TryGetValue(out Tarol.Results.Violation? v)
    && v is Tarol.Results.Error { Code: DataProtectionErrors.SubjectErasedCode });

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

internal static class OutboxNames
{
    public static readonly string[] OnOpened = ["Welcome"];
}
