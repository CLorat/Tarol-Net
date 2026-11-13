using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Time.Testing;
using Tarol.Kernel;
using Tarol.Kernel.AotSmoke;
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

Check("running as native code", !RuntimeFeature.IsDynamicCodeSupported);
var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
var tenant = Id<ITenant>.New(clock);
var user = OperationContext.Start(Actor.User("auth0|5f7c"), tenant, clock);
Check("Start: correlation from the injected clock", user.CorrelationId.Value.ToString()[..8] == Id<ICorrelation>.New(clock).Value.ToString()[..8] && user.CausationId.IsNone);
var saga = user.CausedBy(Id<ICausation>.New(clock)).AsSystem("Sagas.AuctionTimeout");
Check("AsSystem keeps the user and the conversation", saga.Actor == Actor.System("Sagas.AuctionTimeout", "auth0|5f7c") && saga.CorrelationId == user.CorrelationId && saga.Tenant == Option.Some(tenant));

var json = JsonSerializer.Serialize(saga, KernelJsonContext.Default.OperationContext);
Check("JSON (Kernel context): actor first, ids as strings", json.StartsWith("""{"actor":{"kind":"System","id":"Sagas.AuctionTimeout","onBehalfOf":"auth0|5f7c"},"tenant":""", StringComparison.Ordinal));
Check("JSON round-trip", JsonSerializer.Deserialize(json, KernelJsonContext.Default.OperationContext) == saga);
Check("JSON in an app type, app context only", JsonSerializer.Deserialize(JsonSerializer.Serialize(new Audit("bid", saga), SmokeJson.Default.Audit), SmokeJson.Default.Audit) == new Audit("bid", saga));
Check("JSON anonymous, no tenant: nulls", JsonSerializer.Serialize(new OperationContext(Actor.Anonymous, Option.None, user.CorrelationId, Option.None), KernelJsonContext.Default.OperationContext)
    .Contains("\"tenant\":null", StringComparison.Ordinal));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Kernel.AotSmoke
{
    public sealed record Audit(string What, OperationContext Context);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Audit))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
