using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Options.PackageTests;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

// Option<T> from the package alone: same code for value and reference types.
static Option<Order> Find(int quantity) => quantity > 0 ? new Order(quantity) : Option.None;
Check("Option switch over T | None", Find(1) switch { Order => true, None => false } && Find(0) switch { Order => false, None => true });
Check("Option to and from T?", ((Order?)null).ToOption().IsNone && Find(2).ToNullable() == new Order(2) && ((int?)5).ToOption() == Option.Some(5));
Check("Option JSON is the value or null", JsonSerializer.Serialize(new Line(Find(2), default), ConsumerJson.Default.Line) == """{"Order":{"Quantity":2},"Note":null}""");
Check("Option JSON reads null and a missing property as None", JsonSerializer.Deserialize("""{"Order":null}""", ConsumerJson.Default.Line) == new Line(default, default));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Options.PackageTests
{
    public sealed record Order(int Quantity);

    public sealed record Line(Option<Order> Order, Option<string> Note);

    [JsonSerializable(typeof(Order))]
    [JsonSerializable(typeof(Line))]
    internal sealed partial class ConsumerJson : JsonSerializerContext;
}
