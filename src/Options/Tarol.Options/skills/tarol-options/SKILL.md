---
name: tarol-options
description: >-
  Use when a value may be absent in code that uses Tarol.Options or Tarol.Results: returning or consuming Option<T> instead of T? or null,
  creating Some/None, switching over T | None, Map/Bind/Filter/GetValueOrDefault, converting to and from Result<T> or T?,
  Option properties in aggregates, DTOs, events and query results (JSON), and the few edges where T? stays (Minimal API parameters, configuration, raw Npgsql).
  Covers Option<T>, Option, None, OptionExtensions, OptionValueTypeExtensions, the Result conversions and TOPT001-TOPT004.
  Do not decompile the assembly or search the web: the old Tarol.Options T?-extensions (IsSome on every type, int.Some,
  User.None, Or) no longer exist.
---
# Option<T> (Tarol.Options)

## Rules

1. A value that may be absent is `Option<T>` (`using Tarol.Options;`, package `Tarol.Options`, no dependencies). It is a union of `T` and `None`, with the same
   code for value types (`Option<int>`, `Option<Guid>`, `Option<Id<User>>`) and reference types (`Option<User>`).
2. Create: `return user;` (implicit Some), `return Option.None;`, `Option.Some(value)`, `nullable.ToOption()`.
   `default(Option<T>)` is `None`.
3. In a conditional write `found ? value : Option.None`. **Never** `found ? value : default` or `value ?? default`:
   the conditional has the type `T`, so `default` is `0` and becomes `Some(0)` (TOPT004).
4. Consume with `switch` or `is`: `o switch { User u => …, None => … }`. A missing `None` arm is CS8509. Use the case
   patterns `o is User u` / `o is None` or `o.IsSome` / `o.IsNone`, never `o is { }`, `o is not null` or `o == null`:
   those test the struct and are always true or false (TOPT002).
5. Never read `o.Value` (the union's `object`, TOPT001). Use a pattern, `TryGetValue` inside an `if` (ignoring its
   result is TOPT003), `GetValueOrDefault(fallback)`, or `GetValueOrThrow()` where absence would be a bug.
6. `Option.Some(null)` and an implicit conversion from `null` throw `ArgumentNullException` (CS8604 warns first).
   `Option<string?>` and `Option<int?>` are CS8714: use `Option<string>` / `Option<int>`.
7. Absence that is a failure (`GetById`) is a `Result<T>` (package `Tarol.Results`, `using Tarol.Results;`):
   `option.ToResult(Error.NotFound("User.NotFound"))`. `result.ToOption()` goes back and drops the violation.
8. Use `Option<T>` for aggregate and entity properties, events, DTOs and query results. Bind `T?` only where a
   framework cannot read `Option<T>`, and convert there with `ToOption()` / `ToNullable()`: Minimal API query/route
   parameters, configuration (`IOptions<T>`) classes, and raw Npgsql parameters/readers outside Tarol's store.
9. Prefer `switch` to `Match`: a capturing lambda allocates on every call. No async combinators: `await`, then switch.

## Minimal correct example

```csharp
using Tarol.Options;
using Tarol.Results; // only for ToResult / Result<T>

public sealed record User(Guid Id, string Name, Option<string> Nickname, Option<int> Age);

public sealed class Users(Dictionary<string, User> byEmail)
{
    public Option<User> FindByEmail(string email) => byEmail.TryGetValue(email, out var user) ? user : Option.None;

    public Result<User> GetByEmail(string email) => FindByEmail(email).ToResult(Error.NotFound("User.NotFound"));

    public string Greet(string email) => FindByEmail(email) switch
    {
        User user => $"Hello {user.Nickname.GetValueOrDefault(user.Name)}",
        None      => "Hello guest",
    };

    public int AgeOrZero(string email) => FindByEmail(email).Bind(static u => u.Age).GetValueOrDefault(0);
}

// edge: bind T?, convert once
app.MapGet("/users/{email}/age", (string email, int? fallback, Users users)
    => users.FindByEmail(email).Bind(static u => u.Age).GetValueOrDefault(fallback.ToOption().GetValueOrDefault(0)));

// SQL: Npgsql never sees an Option
command.Parameters.Add(new() { Value = (object?)user.Age.ToNullable() ?? DBNull.Value });
Option<int> age = reader.IsDBNull(3) ? Option.None : reader.GetFieldValue<int>(3);
```

## Common mistakes

| Mistake | What happens |
|---|---|
| `int v = option;`, `Take(option)` where a `T` is expected | CS0029 / CS1503 (by design: handle `None` first) |
| `switch { User u => … }` without `None` | CS8509 (an error with warnings as errors) |
| `return found ? id : default;` | `Some(0)` / `Some(Guid.Empty)`; TOPT004 |
| `if (option is { } x)` / `option != null` | always true, `x` is the Option; TOPT002 |
| `option.TryGetValue(out var v); use(v);` | `v` is `default` on None; TOPT003 |
| `option.Value` | boxed `object`; TOPT001 |
| `Option<string?>`, `Option<int?>` | CS8714; at run time a `null` throws |
| `(Option<int> page)` as a Minimal API parameter | inferred as the body: 400 on GET; bind `int?` and call `ToOption()` |
| `Option<int>` property in an `IOptions<T>` class | reflection binder leaves it `None`; source-generated binder does not compile; use `int?` |
| `Value = option` on a `DbParameter`, `GetFieldValue<Option<int>>` | `InvalidCastException` from Npgsql; use `ToNullable()` / `IsDBNull` |
| OpenAPI document for a DTO with `Option<T>` | an empty, required schema; register the Option schema transformer (`T` or `null`, not required) |

## JSON

The value, or `null` for `None`: the same as `T?`. A missing property reads as `None`, and
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` omits `None`. Changing an event field between `T?`
and `Option<T>` keeps stored JSON readable both ways. `{"id":1,"nickname":"ada","age":null}`.
An inner `None` of `Option<Option<T>>` reads back as the outer `None`.

## AOT

Native-AOT safe; nothing to register (the converter is on the type). With a `JsonSerializerContext`, list the DTO and
any `T` that is not a primitive (`[JsonSerializable(typeof(User))]`); `string`, `int`, `long`, `bool`, `decimal`,
`double`, `Guid` and the date/time types resolve from the package. A missing `T` throws `NotSupportedException` naming
the attribute to add.
