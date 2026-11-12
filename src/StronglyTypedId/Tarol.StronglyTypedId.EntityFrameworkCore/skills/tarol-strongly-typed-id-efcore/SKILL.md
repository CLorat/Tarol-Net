---
name: tarol-strongly-typed-id-efcore
description: >-
  Use when mapping Tarol strongly-typed ids (Id<TEntity>) with Entity Framework Core: configuring a DbContext,
  id primary and foreign keys, nullable ids, LINQ queries on ids, or replacing hand-written value converters
  for ids. Gives the two supported configurations and their trade-offs. Do not write per-property
  HasConversion calls or assembly-scanning helpers for ids.
---

# Tarol.StronglyTypedId.EntityFrameworkCore

Namespace `Tarol.StronglyTypedId.EntityFrameworkCore`. Every `Id<T>` / `Id<T>?` property becomes a `Guid` column.

## Choose one

**Whole model, one line** (uses `MakeGenericType` at model building; annotated `RequiresDynamicCode`):

```csharp
services.AddDbContext<ShopContext>(o => o.UseSqlServer(cs).UseStronglyTypedIds());
// or in OnConfiguring: optionsBuilder.UseStronglyTypedIds();
```

**Per marker type, reflection-free:**

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    => builder.ConfigureStronglyTypedId<Customer>().ConfigureStronglyTypedId<Order>();
```

Both cover nullable ids and markers that are interfaces or structs.

## Keys

Ids come from the domain (`Id<T>.New()`), never from the database:

```csharp
modelBuilder.Entity<Order>().Property(o => o.Id).ValueGeneratedNever();
```

## Queries

Compare ids directly; EF translates them to Guid parameters:

```csharp
var lines = await db.Orders.Where(o => o.CustomerId == customerId && o.ParentId == null).ToListAsync(ct);
var order = await db.Orders.FindAsync([orderId], ct);
```

## Mistakes

| Mistake | Fix |
|---|---|
| `.HasConversion(id => id.Value, g => Id<Order>.From(g))` per property | delete it; use one of the two configurations above |
| `o.CustomerId.Value == guid` in a query | compare ids: `o.CustomerId == Id<Customer>.From(guid)` |
| expecting the package to be Native-AOT compatible | it is not (EF Core model building is not) |
