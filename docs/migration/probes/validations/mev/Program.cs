using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using Tarol.Options;
using Tarol.StronglyTypedId;

var sp = new ServiceCollection().AddValidation().BuildServiceProvider();
var options = sp.GetRequiredService<IOptions<ValidationOptions>>().Value;

async Task Run(string name, object value)
{
    if (!options.TryGetValidatableTypeInfo(value.GetType(), out var info)) { Console.WriteLine($"{name}: no type info"); return; }
    var ctx = new ValidateContext { ValidationOptions = options, ServiceProvider = sp };
    long before = GC.GetAllocatedBytesForCurrentThread();
    info.Validate(value, ctx);
    long after = GC.GetAllocatedBytesForCurrentThread();
    Console.WriteLine($"{name}: {after - before} B");
    if (ctx.ValidationErrors is { } errs)
        foreach (var kv in errs) foreach (var e in kv.Value) Console.WriteLine($"   key={kv.Key} path={e.Path} name={e.Name} msg={e.ErrorMessage}");
}

var ok = new PlaceOrder("Ada", "ada@example.com", 3, [new Line("SKU-1", 1)], new Address("Main", "12345"), "nick", Id<Order>.New());
var bad = new PlaceOrder("", "nope", 0, [new Line("x", 1), new Line("", 0)], new Address("", "1"), "n", Id<Order>.Empty);
for (int i = 0; i < 3; i++) { await Run("valid", ok); }
await Run("invalid", bad);
await Run("invalid2", bad);
await Run("emptyIdOnly", ok with { OrderId = Id<Order>.Empty });
await Run("someNickTooLong", ok with { Nickname = "waytoolongnickname" });
var pattern = new Evil(new string('a', 30) + "!");
var sw = System.Diagnostics.Stopwatch.StartNew();
try { await Run("redos", pattern); } catch (Exception ex) { Console.WriteLine($"redos threw {ex.GetType().Name}"); }
Console.WriteLine($"redos took {sw.ElapsedMilliseconds} ms");

public sealed class Order;

[ValidatableType]
public record PlaceOrder(
    [property: Required, StringLength(50, MinimumLength = 2)] string Name,
    [property: Required, EmailAddress] string Email,
    [property: Range(1, 100)] int Quantity,
    [property: MinLength(1)] List<Line> Lines,
    Address Shipping,
    Option<string> Nickname,
    Id<Order> OrderId) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OrderId.IsEmpty) yield return new ValidationResult("must not be empty", [nameof(OrderId)]);
    }
}
public record Line([property: Required] string Sku, [property: Range(1, 10)] int Quantity);
public record Address([property: Required] string Street, [property: RegularExpression(@"^\d{5}$")] string Zip);
[ValidatableType]
public record Evil([property: RegularExpression(@"^(a+)+$")] string Text);
