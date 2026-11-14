# Tarol.Validations

Rules declared once, on value objects. A bundled source generator turns a one-line `record struct` into a validated
value object, and validates every input (command, query, request) that uses it, collecting all field errors with their
input paths as a `Tarol.Results` `Invalid`. Native-AOT ready; the valid path allocates nothing.

## Install

```bash
dotnet add package Tarol.Validations
```

It brings `Tarol.Results`, `Tarol.Options` and `Tarol.StronglyTypedId`.

## 30 seconds

```csharp
using Tarol.Validations;

// the domain: every rule once, one line per concept
public readonly partial record struct Title([Length(3, 80)] string Value);
public readonly partial record struct Quantity([Range(1, 100)] int Value);
public readonly partial record struct Money([Range(Min = 0)] decimal Amount, Currency Currency);

// inputs: the types carry the rules
public sealed partial record AddLine(Id<Order> Order, Id<Product> Product, Quantity Quantity, Option<Title> Note) : IValidatable;

// edge or handler: parse before load
if (command.Validated() is Invalid invalid) return invalid;     // 400: Order: Required; Quantity: Range "1..100"; …
Result<Title> title = Title.Create(request.Title);              // Invalid { : Length "3..80" } or the value

// it still reads like the underlying value
int total = line.Quantity + 1;   string text = title.GetValueOrThrow();
```

What the generator writes for a value object: `Create(…) : Result<T>`, `Check(T, ref ValidationErrors)`, limit constants
(`Quantity.Min`, `Title.MaxLength`), and for one field a conversion to the field, `IComparable<T>` and `<`/`>`,
`ToString` (invariant), and a JSON converter that writes the bare value (`"quantity":3`). For an `IValidatable` record:
`Validate(ref ValidationErrors)`, used by `Validated()`.

## Rules

* **Rules**: `[Range]` (numbers; decimal bounds as strings, `Min =`/`Max =` for one side), `[Length]` (strings),
  `[Pattern]` (regex with a 100 ms timeout; a timeout is a field error), `[Email]`, `[NotEmpty]` (blank text, `Guid.Empty`,
  `Id.Empty`), `[Count]` (arrays, `IReadOnlyList<T>`). Codes in `ValidationCodes`; limits in the error's detail.
* **Custom rule**: `static void Validate(T value, ref ValidationErrors errors)` in the value object; it runs after the
  attribute rules when they passed.
* **The type decides what is required**: `Option<T>` (and `T?`) is optional and its value is validated when present;
  every other member is required (`null`, `Id<T>.Empty`, a missing string value object → `Required`).
* **Paths are input paths** in C# member names: `Lines[1].Quantity`, `Total.Amount`. One error per value (the first
  failing rule).
* **Constructors trust, factories validate.** `new Quantity(500)` and JSON reading do not validate, so old events and
  snapshots always load under stricter rules; live code calls `Create` or `Validated()` (TVAL006 warns on `new`).
* **Name by concept, scoped by namespace**: `Bidders.Name` (3–60) and `Experts.Name` (3–120) are two types with plain
  names; a rule that only one request has (page size, search text) goes on that input's primitive parameter:
  `record SearchAuctions([Length(Max = 100)] string Text, [Range(1, 50)] int PageSize) : IValidatable`.

## JSON

The generated converter is on the type, which reflection and source-generated contexts **in other assemblies** see. A
source-generated context **in the same assembly** cannot see it (generators do not see each other's output) and would
write `{"value":40}`: use `ValueObjectJson.Options(MyJson.Default)` (TVAL009 points at the direct use), or
`options.AddValueObjects()` on options you own.

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| TVAL001 | Error | value object or input (or a containing type) not `partial` |
| TVAL002 | Error | generic or file-local type, or an input inheriting `IValidatable` |
| TVAL003 | Error | rule attribute outside a record's primary constructor |
| TVAL004 | Error | rule does not fit the type (or is on a value-object / input member) |
| TVAL005 | Error | invalid rule arguments (`Min > Max`, a bound the type cannot hold, a pattern that does not parse) |
| TVAL006 | Warning | `new X(…)` / `with` on a value object outside it: use `Create` |
| TVAL007 | Warning | DataAnnotations attribute on a value object or input: no effect |
| TVAL008 | Warning | input member type that cannot be validated (only `null` is checked) |
| TVAL009 | Warning | same-assembly source-generated context used directly: use `ValueObjectJson.Options` |

## Not here

Aggregate invariants (`[Invariant]` methods returning `Result`) come with `Tarol.Primitives`; preconditions of a change
are guard clauses in the aggregate's behaviour returning `Result` (`now` as a parameter); permissions are
`Tarol.Security`. Validation never runs in event application, replay, snapshots or deserialization.

Licence: proprietary, see `LICENSE`.
