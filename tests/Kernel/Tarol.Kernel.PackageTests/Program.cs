using System.Text.Json;
using Tarol.Kernel;
using Tarol.Kernel.Json;
using Tarol.Options;
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

var context = OperationContext.Start(Actor.User("alice"), Id<ITenant>.New(), TimeProvider.System);
var saga = context.CausedBy(Id<ICausation>.New()).AsSystem("Sagas.Checkout");
Check("system actor on behalf of the user", saga.Actor.OnBehalfOf == Option.Some("alice") && saga.CorrelationId == context.CorrelationId);
var json = JsonSerializer.Serialize(saga, KernelJsonContext.Default.OperationContext);
Check("JSON round-trip from the package", JsonSerializer.Deserialize(json, KernelJsonContext.Default.OperationContext) == saga);

// TKRN001 would fire on `DateTimeOffset.UtcNow` here (warnings are errors); the injected clock is the accepted form.
Check("TimeProvider is the clock", TimeProvider.System.GetUtcNow().Year >= 2026);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
