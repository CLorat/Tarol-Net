using System.ComponentModel.DataAnnotations;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using Probe;
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;

BenchmarkSwitcher.FromAssembly(typeof(Bench).Assembly).Run(args);

// (b)/(d) input: a plain DTO with DataAnnotations attributes; (a) the same DTO checked by generated code.
[ValidatableType]
public sealed class PlaceOrderDto
{
    [Required] public Guid? OrderId { get; set; }
    [Required, StringLength(10, MinimumLength = 3)] public string? Name { get; set; }
    [Required, RegularExpression(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] public string? Contact { get; set; }
    [Required] public List<LineDto>? Lines { get; set; }
    [StringLength(10, MinimumLength = 3)] public string? Nickname { get; set; }
    [Required] public PriceDto? Price { get; set; }
}
public sealed class LineDto { [Required] public Guid? Product { get; set; } [Range(1, 100)] public int Quantity { get; set; } }
public sealed class PriceDto { [Range(0d, double.MaxValue)] public decimal Amount { get; set; } public int Currency { get; set; } }

// (a) what a rewritten property-attribute generator would emit for PlaceOrderDto (same runtime as (c)).
public static class PlaceOrderDtoValidation
{
    public static Result Validate(PlaceOrderDto d)
    {
        var e = new Errors();
        if (d.OrderId is null || d.OrderId == Guid.Empty) e.Add("OrderId", Codes.Required, null);
        e.Enter("Name"); Title.Check(d.Name, ref e); e.Exit();
        e.Enter("Contact"); Email.Check(d.Contact, ref e); e.Exit();
        if (d.Lines is null || d.Lines.Count is < 1 or > 50) e.Add("Lines", Codes.Count, "1..50");
        else { e.Enter("Lines"); for (var i = 0; i < d.Lines.Count; i++) { e.EnterIndex(i); var l = d.Lines[i]; if (l.Product is null || l.Product == Guid.Empty) e.Add("Product", Codes.Required, null); e.Enter("Quantity"); Quantity.Check(l.Quantity, ref e); e.Exit(); e.Exit(); } e.Exit(); }
        if (d.Nickname is { } n) { e.Enter("Nickname"); Title.Check(n, ref e); e.Exit(); }
        if (d.Price is null) e.Add("Price", Codes.Required, null); else if (d.Price.Amount < 0) e.Add("Price.Amount", Codes.Range, null);
        return e.ToInvalid() is { } inv ? inv : Result.Success();
    }
}

[MemoryDiagnoser, ShortRunJob]
public class Bench
{
    private readonly ValidationOptions _options; private readonly IServiceProvider _sp;
    private readonly PlaceOrderDto _goodDto, _badDto; private readonly PlaceOrder _goodCmd, _badCmd;
    public Bench()
    {
        _sp = new ServiceCollection().AddValidation().BuildServiceProvider();
        _options = _sp.GetRequiredService<IOptions<ValidationOptions>>().Value;
        _goodDto = new() { OrderId = Guid.NewGuid(), Name = "Lamp", Contact = "a@b.cd", Lines = [new() { Product = Guid.NewGuid(), Quantity = 3 }, new() { Product = Guid.NewGuid(), Quantity = 7 }], Price = new() { Amount = 10 } };
        _badDto = new() { OrderId = null, Name = "x", Contact = "nope", Lines = [new() { Product = Guid.NewGuid(), Quantity = 3 }, new() { Product = null, Quantity = 0 }], Nickname = "n", Price = new() { Amount = -2 } };
        var lines = new[] { new Line(Id<Product>.New(), new Quantity(3)), new Line(Id<Product>.New(), new Quantity(7)) };
        _goodCmd = new(Id<Order>.New(), new Title("Lamp"), new Email("a@b.cd"), lines, Option.None, new Money(10, Currency.Eur));
        _badCmd = new(Id<Order>.Empty, new Title("x"), new Email("nope"), [lines[0], new Line(Id<Product>.Empty, new Quantity(0))], new Title("n"), new Money(-2, Currency.Eur));
    }
    [Benchmark(Baseline = true)] public object C_ValueObjects_Valid() => _goodCmd.Validated();
    [Benchmark] public object C_ValueObjects_Invalid() => _badCmd.Validated();
    [Benchmark] public object A_GeneratedDto_Valid() => PlaceOrderDtoValidation.Validate(_goodDto);
    [Benchmark] public object A_GeneratedDto_Invalid() => PlaceOrderDtoValidation.Validate(_badDto);
    [Benchmark] public object B_MEValidation_Valid() => MeV(_goodDto);
    [Benchmark] public object B_MEValidation_Invalid() => MeV(_badDto);
    [Benchmark] public object D_DataAnnotations_Valid() => Da(_goodDto);
    [Benchmark] public object D_DataAnnotations_Invalid() => Da(_badDto);

    private Result MeV(PlaceOrderDto dto)
    {
        _options.TryGetValidatableTypeInfo(typeof(PlaceOrderDto), out var info);
        var ctx = new ValidateContext { ValidationOptions = _options, ServiceProvider = _sp };
        info!.Validate(dto, ctx);
        if (ctx.ValidationErrors is not { Count: > 0 } errs) return Result.Success();
        var list = new List<FieldError>();
        foreach (var (path, es) in errs) foreach (var e in es) list.Add(new FieldError(path, e.ErrorMessage ?? "Invalid")); // no code available
        return new Invalid(list);
    }
    private static Result Da(PlaceOrderDto dto)  // flat: nested objects and list elements are NOT validated
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true)) return Result.Success();
        var list = new List<FieldError>(results.Count);
        foreach (var r in results) list.Add(new FieldError(r.MemberNames.FirstOrDefault() ?? "", r.ErrorMessage ?? "Invalid"));
        return new Invalid(list);
    }
}
