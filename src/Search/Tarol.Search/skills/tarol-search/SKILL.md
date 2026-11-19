---
name: tarol-search
description: >-
  Use when making Tarol aggregates and their entities searchable, registering global search, or testing a search without a
  database. Covers [Searchable], [SearchTitle], [SearchAudience], [SearchLanguage], SearchWeight, AddSearch, UseInMemorySearch,
  ISearch, SearchQuery, SearchPage, SearchTest, SearchHost, SearchEraser. Do not decompile the package or search the web for it.
---
# Tarol global search

## Rules
* Mark the **aggregate class** `[Searchable("Label")]` (partial, `AggregateRoot<T>`) and the members to find:
  `[Searchable(SearchWeight.A)]` for titles and names, `[Searchable]` (weight B) for descriptions, one `[SearchTitle]` per
  aggregate or entity. Entities reached through `EntitySet<T>`/`EntityRef<T>` with marked members become their own hits
  (type `<stream type>.<entity-name>`), shown and authorized through their aggregate.
* `[PersonalData]` members are searchable **only** as `[Searchable(Exact = true)]`: the whole normalised value is a blind token;
  a part of it does not match. Personal data never lands in the index as text.
* `[SearchAudience]` (string, Guid or a collection) on the aggregate restricts its documents to callers whose subject or roles
  are in it; `[SearchLanguage]` gives the documents' language (ISO 639-1).
* Register once: `builder.AddSearch(s => s.Add<Artwork>().Add<Artist>())` plus an index (`UseInMemorySearch()` in tests,
  `UseNpgsqlSearch()` from Tarol.Search.Npgsql in production).
* **Change a searchable member, weight or flag → raise `s.Version`.** The host compares a generated fingerprint and refuses to
  start otherwise; the new version builds from the start of the feed while the old one keeps answering.
* The tenant always comes from the `OperationContext`; `SearchQuery` has no tenant.

## Example
```csharp
[Searchable("Artwork")]
public partial class Artwork : AggregateRoot<Artwork>
{
    public EntitySet<Edition> Editions { get; } = [];
    [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
    [Searchable] public string Description { get; private set; } = "";
    [PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; }

    public static Artwork Create(string title, string description, string? owner)
    {
        var a = new Artwork(Id<Artwork>.New());
        a.Raise(new Created(title, description, owner));
        return a;
    }

    public Edition AddEdition(string label) => Raise(new Edition.Added(label));
    public sealed record Created(string Title, string Description, [property: PersonalData] string? Owner) : Event;
    private void On(Created e) => (Title, Description, Owner) = (e.Title, e.Description, e.Owner);
}

public partial class Edition : Entity<Artwork, Edition>
{
    [Searchable(SearchWeight.A)] public string Label { get; private set; } = "";
    public sealed record Added(string Label) : Created;
    private void On(Added e) => Label = e.Label;
}

// a test without a database
var test = SearchTest.Create(s => s.Add<Artwork>());
var art = Artwork.Create("Sunset over harbour", "boats at dusk", owner: null);
art.AddEdition("museum edition");
test.Given(art, tenant);
await test.CatchUp();
var page = (await test.Find(new SearchQuery("harb") { Prefix = true, Counts = true }, tenant)).GetValueOrThrow();
// page.Items: hits (Type, Id, Aggregate, Title, Score, Snippet); page.Next: the cursor; page.Counts: per type
```

Query text: words (all must match), `"a phrase"`, `-word`, `a OR b`. `Prefix`, `Counts`, `Snippets`, `Types`, `Audience`,
`Limit` (≤ 50), `After` (the previous page's `Next`; depth ≤ 1,000).

## Common mistakes
* `[Searchable]` on a `[PersonalData]` member without `Exact = true` → **TSRCH003**.
* The aggregate not `partial`, or not an `AggregateRoot<T>` → **TSRCH001**; a member type without text (a dictionary) → **TSRCH002**.
* Two `[SearchTitle]`, or `[SearchAudience]`/`[SearchLanguage]` on an entity → **TSRCH004**; marked class without marked members → **TSRCH005** (warning).
* Weights changed without raising the version → `InvalidOperationException` "raise the version" when the host starts.
* No index registered → `InvalidOperationException` naming `UseInMemorySearch()`/`UseNpgsqlSearch()`.
* Expecting row rules (`ChangeHandler<,>`) to filter hits: they do not run on search; use `[SearchAudience]`.
* A key's tombstone alone leaves the blind token findable: register `AddDataProtection` so `SearchEraser` re-indexes erased subjects.

## AOT
The generator emits plain code (no reflection); `Tarol.Search` is Native-AOT compatible. Erasure needs a key store
(`IKeyStore`) and the event codec, as for sealing.
