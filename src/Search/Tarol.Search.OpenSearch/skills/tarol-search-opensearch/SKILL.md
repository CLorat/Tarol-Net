---
name: tarol-search-opensearch
description: >-
  Use when running Tarol global search on OpenSearch. Covers UseOpenSearch, OpenSearchOptions, OpenSearchIndex,
  UseNpgsqlSearchCheckpoints, ISearchCheckpoints, readiness. Do not add an OpenSearch or Elasticsearch SDK, decompile the
  package or search the web for it.
---
# Tarol search on OpenSearch

## Rules
* Register after `AddSearch`: `.UseNpgsqlSearchCheckpoints().UseOpenSearch(provider => client, options)`. The checkpoints must
  live somewhere durable: without `ISearchCheckpoints` the start fails with a message naming `UseNpgsqlSearchCheckpoints()`.
* The `HttpClient` carries the cluster address and the authentication (a `DelegatingHandler`, a header from configuration).
  Never put credentials in code or in committed settings.
* The index of version n is `<IndexPrefix>-v<n>` (lowercase). Raising `s.Version` builds a new index; the newest current version
  answers meanwhile. Delete an old index yourself once the new one is current.
* OpenSearch shows a write after its refresh (about 1 s). For read-your-writes set `WaitForRefresh = true` (each batch waits).
* Counts per type are exact; ranking is BM25 with the weights A–D as boosts (10, 4, 2, 1).

## Example
```csharp
builder.Services.AddHttpClient("search", c => c.BaseAddress = new Uri(configuration["Search:Url"]!));
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Artwork>())
    .UseNpgsqlStore(dataSource)
    .UseNpgsqlReadModels()
    .AddSearch(s => s.Add<Artwork>())
    .UseNpgsqlSearchCheckpoints()
    .UseOpenSearch(p => p.GetRequiredService<IHttpClientFactory>().CreateClient("search"),
        new OpenSearchOptions { IndexPrefix = "artflow", Replicas = 1 });
```

## Common mistakes
* No `UseNpgsqlSearchCheckpoints()` → `InvalidOperationException` at the first resolve.
* Expecting a search right after the commit to see it without `WaitForRefresh` (the refresh interval).
* A cluster whose disk is above its flood-stage watermark refuses index creation (`index_create_block_exception`): an
  operator setting of the cluster, not of Tarol.

## AOT
HttpClient and `Utf8JsonWriter`/`JsonDocument`; 0 IL warnings (`Tarol.Search.OpenSearch.AotSmoke`).
