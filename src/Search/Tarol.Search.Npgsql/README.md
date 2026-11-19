# Tarol.Search.Npgsql

PostgreSQL full-text search for [Tarol.Search](../Tarol.Search/README.md), in the event store's own database: no extra
server, documents and the index's checkpoint written in one transaction.

```csharp
builder.UseNpgsqlStore(dataSource)
       .UseNpgsqlReadModels()                          // the gate (one runner per version) and the feed signal
       .AddSearch(search => search.Add<Artwork>())
       .UseNpgsqlSearch();
```

* One table per index version, `<schema>.search_v<n>`, with a `tsvector` generated from each document's language: German,
  English or simple, accents folded (`Café` finds `cafe`). A GIN led by the tenant (`btree_gin`), ranking within the first
  1,000 matches (the measured design: 16 ms p50 for a common word among 1,000,000 documents in a 10 % tenant).
* A typo fallback on titles (`pg_trgm`, word similarity ≥ 0.4) when full text finds nothing; prefix on the last word; counts
  per type up to 1,000; snippets (`ts_headline`); blind exact tokens for personal data.
* The checkpoint is the row (`search`, n) of `projections`: the lag gauge and `DropVersion` of Tarol.Operations see it. While a
  new version builds, the newest current version answers.
* Needs the extensions `pg_trgm`, `btree_gin` and `unaccent`. They are trusted: the owner of the database creates them on the
  first start; otherwise the error names them for an administrator.

Licence: AGPL-3.0-only, or the owner's commercial licence.
