# Tarol.Search.OpenSearch

The OpenSearch adapter of [Tarol.Search](../Tarol.Search/README.md): the same documents, tenant rules, query syntax and
contract as the PostgreSQL index, in an OpenSearch cluster (Apache-2.0).

```csharp
builder.UseNpgsqlStore(dataSource)
       .UseNpgsqlReadModels()
       .AddSearch(search => search.Add<Artwork>())
       .UseNpgsqlSearchCheckpoints()                                   // Tarol.Search.Npgsql: the checkpoints stay in projections
       .UseOpenSearch(_ => new HttpClient { BaseAddress = new Uri("https://search.internal:9200") });
```

* One index per version, `<prefix>-v<n>`: tenant, type, aggregate, exact tokens and audience as keywords; the text per weight
  with accent folding and `.en`/`.de` sub-fields with English and German stemming; the generated fingerprint in the mapping's
  `_meta` (a changed shape without a version bump is refused).
* Queries come from Tarol's own parser (phrases, `-word`, `OR`, a prefix on the last word), ranked by BM25 with the weights as
  boosts; a fuzzy typo fallback decided by the shared one-edit rule; exact counts per type; highlights.
* The documents are written first, then the checkpoint moves (at least once; a document is the current state, so a repeat changes
  nothing). `WaitForRefresh` makes each batch visible before the checkpoint moves (read-your-writes, about a second per batch).
* No SDK: HttpClient with hand-written JSON, Native-AOT ready. Authentication is the `HttpClient`'s (a handler, a header);
  no credential is stored by Tarol. A failed connection is a `ready` problem until the cluster answers again.

Licence: AGPL-3.0-only, or the owner's commercial licence. OpenSearch itself is Apache-2.0.
