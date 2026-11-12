---
name: tarol-strongly-typed-id
description: >-
  Use when writing or reviewing C# code that uses Tarol.StronglyTypedId (Id<TEntity>): creating, parsing,
  formatting or comparing ids, putting them in DTOs, JSON, routes, configuration or TypeDescriptor, writing
  tests with deterministic ids, or fixing diagnostic TSID001. Covers the exact API, what works without
  configuration, the Native-AOT rules and the mistakes to avoid. The full API is here: do not decompile the
  assembly or search the web for it.
---

# Tarol.StronglyTypedId

`Id<TEntity>` is a `readonly record struct` over one `Guid` (namespace `Tarol.StronglyTypedId`). The type
argument is a phantom marker (any class, record, struct or interface); it only exists at compile time.

## Rules

1. Create ids with `Id<T>.New()` (UUIDv7, time-ordered). There is no public constructor.
2. Convert explicitly: `Id<T>.From(guid)` or `(Id<T>)guid`; back with `id.Value` or `(Guid)id`. There is no
   implicit conversion, on purpose. Never call `UnsafeAs<TOther>()` except at a real domain boundary.
3. Parse with `Id<T>.Parse(string)` / `Id<T>.TryParse(string?, out Id<T>)` (any Guid format), or the
   `ISpanParsable`/`IUtf8SpanParsable` overloads `Parse(ReadOnlySpan<char|byte>, IFormatProvider?)`.
4. `Id<T>.Empty` (= `default`) is a valid value everywhere input arrives (parse, JSON, binding). Guard with
   `id.IsEmpty` or `id.ThrowIfEmpty()` (throws `ArgumentException` naming the argument).
5. Do **not** add `[JsonConverter]`, a `JsonConverterFactory`, a `TypeConverter` attribute or a registration
   call for ids. JSON uses the built-in `IdJsonConverter<T>`; `TypeDescriptor` registrations are generated.
6. Deterministic ids in tests: `Id<T>.New(DateTimeOffset)` or `Id<T>.New(TimeProvider)`.

## Minimal correct example

```csharp
using System.Text.Json.Serialization;
using Tarol.StronglyTypedId;

public sealed class Customer;
public sealed record OrderDto(Id<OrderDto> Id, Id<Customer> CustomerId, Dictionary<Id<Customer>, int> Seats);

[JsonSerializable(typeof(OrderDto))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

var dto = new OrderDto(Id<OrderDto>.New(), Id<Customer>.New(), []);
string json = JsonSerializer.Serialize(dto, AppJsonContext.Default.OrderDto); // ids are plain "D" strings

app.MapGet("/customers/{id}", (Id<Customer> id) => …);  // binds through ISpanParsable

if (Id<Customer>.TryParse(input, out var customerId) && !customerId.IsEmpty) { … }
```

## Behaviour to rely on

| Area | Behaviour |
|---|---|
| JSON | value: `"xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"`; also as dictionary key. Reading accepts only the "D" format; anything else throws `JsonException`. Works with reflection, source-generated contexts and Native AOT. |
| Equality, hashing, ordering | those of the `Guid`; `<`, `>`, `<=`, `>=` and `IComparisonOperators<Id<T>, Id<T>, bool>` exist. v7 ids sort by creation time. |
| Formatting | `ToString()` is "D"; `ToString("N")`, `TryFormat(Span<char>…)`, `TryFormat(Span<byte>…)` (UTF-8). |
| TypeDescriptor | The bundled generator registers `IdTypeConverter<X>` for every `Id<X>` the assembly spells **or inherits** (e.g. `class Order : AggregateRoot<Order>` inheriting `Id<Order> Id`). |
| Configuration | The reflection binder binds `Id<T>` properties. The **source-generated** binder (Native AOT) does not: bind a `Guid` and use `Id<T>.From`, or `Id<T>.Parse(configuration["Key"]!)`. |

## Native AOT

* Use a `JsonSerializerContext`; nothing id-specific is needed.
* Use `TypeDescriptor.GetConverterFromRegisteredType(typeof(Id<X>))`, not `TypeDescriptor.GetConverter(Type)`
  (IL2026).
* Never use `typeof(Id<>).MakeGenericType(...)` in application code.

## Common mistakes

| Mistake | Consequence / fix |
|---|---|
| `new Id<User>(guid)` | does not compile (internal ctor): use `Id<User>.From(guid)` |
| `Guid g = id;` / `Id<User> id = guid;` | does not compile: explicit conversion |
| passing `Id<Order>` where `Id<User>` is expected | compile error: the point of the type |
| `Id<T>.New` used as a property | it is a method: `Id<T>.New()` |
| marker is a `private` nested or `file` type | TSID001 (info): no `TypeDescriptor` registration; JSON still works. Make the marker `internal` or `public`, or call `IdConverterRegistry.Register<T>()` once at startup. |
| `Id<T>` property in an options class bound in a Native-AOT app | stays `Empty`: bind `Guid` instead (see Configuration) |
| relying on `Empty` being rejected by parsing or JSON | it is accepted: check `IsEmpty` |
