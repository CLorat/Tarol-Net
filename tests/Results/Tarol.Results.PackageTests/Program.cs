using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Results;
using Tarol.Results.PackageTests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

static Result<Order> Place(int quantity) => quantity > 0 ? new Order(quantity) : Invalid.Field("Quantity", "Quantity.Positive");

Check("switch over the package's union", Place(2) switch { Order o => o.Quantity == 2, Violation => false });
Check("Invalid case", Place(0) is Invalid { Errors: [{ Code: "Quantity.Positive" }] });
Check("default is Error.Uninitialized", default(Result<Order>) is Error { Code: "Result.Uninitialized" });

var json = JsonSerializer.Serialize(Place(0), ConsumerJson.Default.ResultOrder);
Check("JSON contract", json == """{"failure":{"kind":"invalid","errors":[{"field":"Quantity","code":"Quantity.Positive"}]}}""");
Check("JSON round-trip", JsonSerializer.Deserialize(json, ConsumerJson.Default.ResultOrder) == Place(0));

// TRES002 would fire on `Place(1);` (warnings are errors here); the explicit discard is the accepted form.
_ = Place(1);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Results.PackageTests
{
    public sealed record Order(int Quantity);

    [JsonSerializable(typeof(Result<Order>))]
    [JsonSerializable(typeof(Order))]
    internal sealed partial class ConsumerJson : JsonSerializerContext;
}
