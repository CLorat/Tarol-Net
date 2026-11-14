---
name: tarol-validations
description: >-
  Use when writing input validation or value objects with Tarol.Validations: declaring a rule once on a
  readonly partial record struct ([Range], [Length], [Pattern], [Email], [NotEmpty], [Count], a custom static Validate),
  validating commands/queries with IValidatable and Validated(), reading Invalid/FieldError paths, Option/Id/collection
  members, JSON of value objects (ValueObjectJson), and fixing TVAL001-TVAL009. Do not hand-write validators per DTO,
  use DataAnnotations or FluentValidation on Tarol inputs, validate in constructors, or decompile/web-search: the old
  [GenerateValidation], ValidateValues(), [Invariant] bool rules and IPrerequisite no longer exist.
---
# Tarol.Validations

## Rules

1. **One rule, one place: the value object.** `public readonly partial record struct Title([Length(3, 80)] string Value);`
   (`using Tarol.Validations;`). Every command, event and aggregate uses `Title`; never repeat `[Length]` on a DTO.
2. **Name by concept, scope by namespace.** Different limits = different types with plain names in their aggregate's
   namespace (`Bidders.Name`, `Experts.Name`); a shared rule is one type in the context root (`Email`, `Money`).
3. **Per-request limits** (page size, search text) go on the primitive parameter of that input:
   `record SearchAuctions([Length(Max = 100)] string Text, [Range(1, 50)] int PageSize) : IValidatable`.
4. **Inputs** are `sealed partial record X(…) : IValidatable`. Call `x.Validated()` → `Result<X>`: the input, or one
   `Invalid` with every field error (`Lines[1].Quantity: Range "1..100"`). Validate **before** loading the aggregate.
5. **Create live, trust on load.** Live code: `Title.Create(raw)` → `Result<Title>`. `new Title(…)` and JSON reading do
   not validate (old events must load); `new` outside the type is TVAL006.
6. **The type decides required/optional.** `Option<T>` and `T?` are optional, validated when present. Every other member
   is required: `null`, `Id<T>.Empty`, a missing string value object → `Required`. A missing `int` value object reads as
   `0` and reports its own rule.
7. **Rules**: `[Range(1, 100)]`, `[Range("0.01", "9999.99")]` (decimal), `[Range(Min = 0)]`; `[Length(3, 80)]`,
   `[Length(Max = 100)]`; `[Pattern(@"^\d{5}$")]` (100 ms timeout, timeout = `Pattern` error); `[Email]`; `[NotEmpty]`
   (blank, `Guid.Empty`, `Id.Empty`); `[Count(1, 50)]` on arrays/`IReadOnlyList<T>`. Codes: `ValidationCodes`. One error
   per value: the first failing rule.
8. **Custom rule**: `private static void Validate(string value, ref ValidationErrors errors) { if (…) errors.Add("Iban.Checksum"); }`
   inside the value object; it runs after the attribute rules passed. Parameter type: the field (one field) or the value object.
9. **Arithmetic** works on the field (implicit conversion): `int sum = a + b;` gives an `int`; back into the domain only
   via `Quantity.Create(sum)`. Comparisons and `Max()`/`OrderBy` work on the value object.
10. **JSON**: one-field value objects write the bare value. A source-generated `JsonSerializerContext` in the **same**
    assembly must be wrapped: `var options = ValueObjectJson.Options(ShopJson.Default);` then
    `JsonSerializer.Serialize(e, options.GetTypeInfo<OrderPlaced>())`. On options you own: `options.AddValueObjects()`.

## Example

```csharp
using Tarol.Options;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

public readonly partial record struct Quantity([Range(1, 100)] int Value);
public readonly partial record struct Title([Length(3, 10)] string Value);
public sealed partial record Line(Id<Product> Product, Quantity Quantity) : IValidatable;
public sealed partial record PlaceOrder(Id<Order> OrderId, Title Name, [Count(1, 50)] IReadOnlyList<Line> Lines, Option<Title> Note) : IValidatable;

public static Result Handle(PlaceOrder command)
{
    if (command.Validated() is Invalid invalid)
        return invalid;                                   // e.g. OrderId: Required; Lines[1].Quantity: Range "1..100"
    // load (404), permission (403), behaviour guards (409), commit
    return Result.Success();
}

// tests: no host, no DI
Quantity.Create(0).ShouldBe(Invalid.Field("", ValidationCodes.Range, "1..100"));
```

## Common mistakes

| Mistake | Result |
|---|---|
| `readonly record struct Zip([Pattern(…)] string Value)` without `partial` | TVAL001 |
| `record struct Box<T>([Range(1, 2)] int Value)`, a `file` type, an input deriving another input | TVAL002 |
| `[Length]` on a method parameter or a class primary constructor | TVAL003 |
| `[Length]` on an `int`, `[Range]` on a string, `[Range]` on a member whose type is a value object | TVAL004 |
| `[Range(5, 1)]`, `[Range(0, 300)] byte`, `[Range(0.5, 2)] int`, an unparsable pattern | TVAL005 |
| `new Quantity(500)` or `q with { Value = 7 }` in live code | TVAL006 (fixtures: `#pragma warning disable TVAL006`) |
| `[MaxLength(5)]` / `[Required]` (DataAnnotations) on a value object or input | TVAL007: no effect; use `Length`, or the member type |
| `Dictionary<,>`, a plain class or nested lists as input members | TVAL008: only `null` is checked |
| `JsonSerializer.Serialize(e, ShopJson.Default.OrderPlaced)` with a same-assembly value object | TVAL009: it writes `{"value":40}` |
| validating in a constructor, an `On(event)` handler or a JSON converter | old events stop loading; validate in `Create` / `Validated()` only |
| a rule that needs "now" or a read (end time in the future, artwork verified) | not a value rule: a guard in the behaviour (`now` parameter) or the handler |

## AOT

Generated code uses no reflection; JSON goes through generated converters (`ValueObjectJson` registers them from module
initializers). Patterns use `Regex` with a timeout (no `RegexOptions.Compiled`). 0 IL warnings under Native AOT.
