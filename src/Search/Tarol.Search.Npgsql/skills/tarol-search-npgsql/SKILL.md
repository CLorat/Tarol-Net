---
name: tarol-search-npgsql
description: >-
  Use when running Tarol global search on PostgreSQL. Covers UseNpgsqlSearch, NpgsqlSearchOptions, NpgsqlSearchIndex,
  NpgsqlSearchSchema, languages, the extensions it needs, versions and read-after. Do not decompile the package or search the web for it.
---
# Tarol search on PostgreSQL

## Rules
* Register after `AddSearch`: `builder.UseNpgsqlStore(dataSource).UseNpgsqlReadModels().AddSearch(s => s.Add<Artwork>()).UseNpgsqlSearch();`
  `UseNpgsqlReadModels` brings the one-runner gate and the feed signal; the index uses the store's schema by default.
* The database user must own the database (or an administrator creates `pg_trgm`, `btree_gin`, `unaccent` once). A missing
  privilege fails the first start with an `InvalidOperationException` naming the three extensions.
* Languages: a `[SearchLanguage]` member with "de" or "en" stems German or English; anything else is `simple`. Accents are
  folded in every language. Pass the query's language (`SearchQuery.Language`) to stem the query the same way.
* Counts stop at 1,000 matches (`SearchPage.CountsCapped`), ranking considers the first 1,000 matches, the cursor reaches 1,000 hits.
* A search after the caller's own change: wait for `SearchHost.WaitFor(position)` (HTTP: `Tarol-Read-After`); PostgreSQL shows a
  document as soon as the host's batch commits.
* A new index version (`s.Version = 2`) builds in `search_v2` while `search_v1` answers; drop the old one with `DropVersion("search", 1)`.

## Example
```csharp
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
builder.UseNpgsqlStore(dataSource)
       .UseNpgsqlReadModels()
       .AddSearch(search => { search.Add<Artwork>().Add<Artist>(); search.Version = 1; })
       .UseNpgsqlSearch(new NpgsqlSearchOptions { TypoThreshold = 0.4 });

var page = (await search.Search(new SearchQuery("häusern") { Language = "de", Counts = true }, context, ct)).GetValueOrThrow();
```

## Common mistakes
* A data source without `EnableArrays()` → Npgsql refuses the array parameters (the store needs them too).
* Expecting `unaccent()` in your own generated columns: PostgreSQL refuses it there (42P17); the Tarol configurations
  (`<schema>.german`, `.english`, `.simple`) fold accents instead.
* Reading `CountsCapped == false` as an exact total beyond 1,000: the count stops at the bound.

## AOT
Plain Npgsql with a slim data source; 0 IL warnings (`Tarol.Search.Npgsql.AotSmoke`).
