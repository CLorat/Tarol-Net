using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Persistence.PackageTests;
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

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
