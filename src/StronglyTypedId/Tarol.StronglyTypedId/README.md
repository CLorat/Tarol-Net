# Tarol.StronglyTypedId

`Id<TEntity>`: a strongly-typed identifier over a `Guid`. `Id<User>` and `Id<Order>` are different types, so
passing one where the other is expected does not compile. At run time it is exactly a `Guid` (16 bytes, no
allocation, same comparison and hashing).

```bash
dotnet add package Tarol.StronglyTypedId
```

```csharp
using Tarol.StronglyTypedId;

public sealed class User { public Id<User> Id { get; } = Id<User>.New(); }

var id = Id<User>.New();                    // UUIDv7: time-ordered, index-friendly
var same = Id<User>.Parse(id.ToString());   // also TryParse, span and UTF-8 overloads
Guid raw = (Guid)id;                        // conversions are explicit on purpose
var back = Id<User>.From(raw);
```

## What works without configuration

| Scenario | How |
|---|---|
| System.Text.Json (reflection and source-generated contexts, Native AOT) | `[JsonConverter(typeof(IdJsonConverter<>))]` on the type: ids are plain strings, also as dictionary keys |
| Minimal APIs and MVC route/query binding | `ISpanParsable<Id<T>>` |
| `TypeDescriptor` (`GetConverter`, `GetConverterFromRegisteredType`), reflection-based configuration binding | the bundled source generator registers `IdTypeConverter<T>` for every id your assembly uses |
| Sorting, generic math (`IComparisonOperators`) | ordering of the underlying `Guid`; v7 ids sort by creation time |
| Formatting | `ToString()`, `ToString(format)`, `TryFormat` to `char` and UTF-8 spans |

Guards: `id.IsEmpty`, `id.ThrowIfEmpty()`. `Id<T>.Empty` (all zeros) is a valid value on every inbound path
(parse, JSON, binding); reject it where it matters.

Deterministic ids for tests and backfills: `Id<T>.New(DateTimeOffset)` and `Id<T>.New(TimeProvider)`.

## Native AOT

The package is AOT-compatible and trimming-safe. Notes:

* JSON: use a `JsonSerializerContext`; ids need no extra attribute.
* `TypeDescriptor`: use `TypeDescriptor.GetConverterFromRegisteredType(typeof(Id<X>))`; the generator registers
  every id your assembly uses (also ids inherited from a generic base such as `AggregateRoot<Order>`).
* **Configuration binding**: the *source-generated* configuration binder (the one AOT uses) does not use
  `TypeConverter`s and leaves `Id<T>` properties empty. Bind a `Guid` and convert with `Id<T>.From`, or
  `Id<T>.Parse(configuration["Key"])`.

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| TSID001 | Info | `Id<X>` with a private or file-local marker `X` cannot be registered for `TypeDescriptor`; JSON is unaffected. |

## Entity Framework Core

See [Tarol.StronglyTypedId.EntityFrameworkCore](https://www.nuget.org/packages/Tarol.StronglyTypedId.EntityFrameworkCore).

## For coding agents

This package ships the agent skill `skills/tarol-strongly-typed-id/SKILL.md`.
