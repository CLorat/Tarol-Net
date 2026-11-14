using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;
using Tarol.Validations.AotSmoke;

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
Check("Create: valid", Quantity.Create(40) is Quantity { Value: 40 });
Check("Create: invalid with code and limits", Quantity.Create(0) == Invalid.Field("", ValidationCodes.Range, "1..100"));
Check("arithmetic on the underlying value", Quantity.Create(40).GetValueOrThrow() + Quantity.Create(2).GetValueOrThrow() == 42);

var watch = Stopwatch.StartNew();
Check("pattern timeout is a field error", Evil.Create(new string('a', 30) + "!") == Invalid.Field("", ValidationCodes.Pattern));
Check("pattern timeout is bounded", watch.Elapsed < TimeSpan.FromSeconds(2));

var line = new Line(Id<Product>.New(), Quantity.Create(3).GetValueOrThrow());
var order = new PlaceOrder(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), [line], Option.None);
Check("input: valid", order.Validated().IsSuccess);
#pragma warning disable TVAL006 // invalid on purpose
var bad = new PlaceOrder(Id<Order>.Empty, new Title("x"), [line, new Line(Id<Product>.Empty, new Quantity(0))], new Title("n"));
#pragma warning restore TVAL006
Check("input: every field with its path", bad.Validated() == new Invalid(
    new FieldError("OrderId", "Required"), new FieldError("Name", "Length", "3..10"),
    new FieldError("Lines[1].Product", "Required"), new FieldError("Lines[1].Quantity", "Range", "1..100"),
    new FieldError("Nickname", "Length", "3..10")));

var options = ValueObjectJson.Options(SmokeJson.Default);
var placed = new OrderPlaced(Title.Create("Lamp").GetValueOrThrow(), Quantity.Create(40).GetValueOrThrow(), Option.None);
var json = JsonSerializer.Serialize(placed, options.GetTypeInfo<OrderPlaced>());
Check("JSON: bare values through the resolver", json == """{"name":"Lamp","total":40,"bonus":null}""");
var old = JsonSerializer.Deserialize("""{"name":"A title that is far too long","total":500,"bonus":7}""", options.GetTypeInfo<OrderPlaced>())!;
Check("JSON: old data under stricter rules loads", old.Total.Value == 500 && old.Name.Value.Length > 10 && old.Bonus == Option.Some(Quantity.Create(7).GetValueOrThrow()));

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Validations.AotSmoke
{
    public sealed class Order;

    public sealed class Product;

    public readonly partial record struct Quantity([Range(1, 100)] int Value);

    public readonly partial record struct Title([Length(3, 10)] string Value);

    public readonly partial record struct Evil([Pattern(@"^(a+)+$", TimeoutMilliseconds = 50)] string Value);

    public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;

    public sealed partial record PlaceOrder(Id<Order> OrderId, Title Name, [Count(1, 50)] IReadOnlyList<Line> Lines, Option<Title> Nickname) : IValidatable;

    public sealed record OrderPlaced(Title Name, Quantity Total, Option<Quantity> Bonus);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(OrderPlaced))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
