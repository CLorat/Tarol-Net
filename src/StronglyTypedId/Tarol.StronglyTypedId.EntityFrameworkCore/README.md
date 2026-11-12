# Tarol.StronglyTypedId.EntityFrameworkCore

Maps every `Id<TEntity>` and `Id<TEntity>?` property of an EF Core model to a `Guid` column (the provider's
native uuid type where it has one), including ids with interface or struct markers, and translates them in LINQ queries.

```bash
dotnet add package Tarol.StronglyTypedId.EntityFrameworkCore
```

## One line for the whole model

```csharp
using Tarol.StronglyTypedId.EntityFrameworkCore;

services.AddDbContext<ShopContext>(o => o.UseNpgsql(connectionString).UseStronglyTypedIds());
```

`UseStronglyTypedIds()` replaces EF Core's `IValueConverterSelector` with one that offers
`IdValueConverter<T>` for every id type it meets. It is annotated `[RequiresDynamicCode]` /
`[RequiresUnreferencedCode]`.

## Reflection-free, per type

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    => builder.ConfigureStronglyTypedId<Customer>().ConfigureStronglyTypedId<Order>();
```

## Keys

Ids are created by the domain (`Id<T>.New()`, UUIDv7). Configure id keys with `ValueGeneratedNever()` so EF never
expects the database to generate them.

## Native AOT

Not AOT-compatible: EF Core model building is not. Use the per-type registration together with a compiled
model if you need to reduce reflection.
