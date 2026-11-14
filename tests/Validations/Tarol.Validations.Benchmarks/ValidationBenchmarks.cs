using BenchmarkDotNet.Attributes;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Validations.Benchmarks;

public sealed class Order;

public sealed class Product;

public readonly partial record struct Quantity([Range(1, 100)] int Value);

public readonly partial record struct Title([Length(3, 10)] string Value);

public readonly partial record struct Email([Email] string Value);

public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;

public sealed partial record PlaceOrder(Id<Order> OrderId, Title Name, Email Contact, [Count(1, 50)] IReadOnlyList<Line> Lines, Option<Title> Nickname) : IValidatable;

[MemoryDiagnoser]
[ShortRunJob]
public class ValidationBenchmarks
{
    private readonly PlaceOrder _valid;
    private readonly PlaceOrder _invalid;
    private readonly int _good = 5;
    private readonly int _bad = -1;
    private readonly string _email = "ada@example.com";

    public ValidationBenchmarks()
    {
        Line[] lines = [new(Id<Product>.New(), Quantity.Create(3).GetValueOrThrow()), new(Id<Product>.New(), Quantity.Create(7).GetValueOrThrow())];
        _valid = new PlaceOrder(Id<Order>.New(), Title.Create("Lamp").GetValueOrThrow(), Email.Create("a@b.cd").GetValueOrThrow(), lines, Option.None);
#pragma warning disable TVAL006 // invalid on purpose
        _invalid = new PlaceOrder(Id<Order>.Empty, new Title("x"), new Email("nope"), [lines[0], new Line(Id<Product>.Empty, new Quantity(0))], new Title("n"));
#pragma warning restore TVAL006
    }

    [Benchmark(Baseline = true)]
    public bool Quantity_Create_Valid() => Quantity.Create(_good).IsSuccess;

    [Benchmark]
    public bool Quantity_Create_Invalid() => Quantity.Create(_bad).IsSuccess;

    [Benchmark]
    public bool Email_Create_Valid() => Email.Create(_email).IsSuccess;

    [Benchmark]
    public bool Input_Validated_Valid() => _valid.Validated().IsSuccess;

    [Benchmark]
    public bool Input_Validated_Invalid() => _invalid.Validated() is Invalid { Errors.Count: 6 };
}
