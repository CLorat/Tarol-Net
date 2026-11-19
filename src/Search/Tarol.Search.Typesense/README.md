# Tarol.Search.Typesense

The Typesense adapter of [Tarol.Search](../Tarol.Search/README.md): the same documents, tenant rules, query syntax and contract
as the PostgreSQL index, in a Typesense server (the server is GPL-3.0 and runs as a separate process; this package is Tarol's
own HTTP client).

```csharp
var key = configuration["Search:Typesense:ApiKey"]!;                           // from configuration or a secret store, never code
builder.UseNpgsqlStore(dataSource)
       .UseNpgsqlReadModels()
       .AddSearch(search => search.Add<Artwork>())
       .UseNpgsqlSearchCheckpoints()
       .UseTypesense(_ => TypesenseTarolBuilderExtensions.Client(new Uri("http://search.internal:8108"), key));
```

* One collection per version, `<prefix>_v<n>`; the fingerprints in `<prefix>_versions`.
* **Fields:**
  * tenant, type, aggregate, exact tokens and audience are filter fields;
  * the text per weight is folded and stemmed in English, and German documents also go into `*_de` fields with German stemming.
* **Queries come from Tarol's own parser.** Typesense has no `OR` and tokenizes a query by its first field's locale. The
  alternatives, the locales and the exact token therefore run as one `multi_search`, and their hits are merged by score.
* **Typos and results:** typo matching first runs exact, then falls back by the shared one-edit rule. Counts per type and
  highlights are included.
* **Writes are synchronous.** Imports are searchable when they return, and then the checkpoint moves (at least once, harmless to
  repeat).
* **No SDK.** HttpClient with hand-written JSON, Native-AOT ready. A failed connection is a `ready` problem until the server
  answers again.

Licence: AGPL-3.0-only, or the owner's commercial licence.
