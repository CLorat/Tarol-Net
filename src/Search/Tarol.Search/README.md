# Tarol.Search

Global search for Tarol: one search box over every aggregate and entity a user may see, ranked, paged, restricted to the
caller's tenant and to the types (and rows) they may read.

* Mark members of an aggregate's state and of its entities with `[Searchable]` (weight A–D), one `[SearchTitle]`, an optional
  `[SearchAudience]` and `[SearchLanguage]`. The generator inside this package writes the documents from the **loaded
  aggregate**: no reflection, Native-AOT ready. An entity is found by its own fields and shown through its aggregate.
* `SearchHost` feeds the index from the event feed like a projection: checkpoint, one runner per version, lag, `CatchUp`,
  `WaitFor`. Raise the index version when the searchable members change; a generated fingerprint refuses a forgotten bump.
* Personal data is searchable only as an exact blind token (`[PersonalData, Searchable(Exact = true)]`), never as text, and the
  index takes part in erasure (re-index of the subject's streams) and retention (`SearchEraser`).
* `ISearch` searches the tenant of the caller's context. The port `ISearchIndex` has an in-memory implementation here
  (`UseInMemorySearch`, `SearchTest`); PostgreSQL full-text search is `Tarol.Search.Npgsql`, OpenSearch and Typesense have
  their own packages.

```csharp
[Searchable("Artwork")]
public partial class Artwork : AggregateRoot<Artwork>
{
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";
    public EntitySet<Edition> Editions { get; } = [];
}

builder.AddSearch(search => search.Add<Artwork>()).UseInMemorySearch();

var page = await search.Search(new SearchQuery("harbour") { Prefix = true, Counts = true }, context, cancellationToken);
```

Query syntax: words (all must match), `"a phrase"`, `-word`, `a OR b`; `Prefix` makes the last word a prefix, a typo fallback
runs when nothing matches. Pages of at most 50, a cursor down to 1,000 hits, counts per type up to 1,000.

Design: [docs/architecture/search.md](https://github.com/CLorat/Tarol-Net/blob/main/docs/architecture/search.md). Licence:
AGPL-3.0-only, or the owner's commercial licence.
