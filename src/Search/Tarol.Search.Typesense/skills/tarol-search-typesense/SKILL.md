---
name: tarol-search-typesense
description: >-
  Use when running Tarol global search on Typesense. Covers UseTypesense, TypesenseTarolBuilderExtensions.Client,
  TypesenseOptions, TypesenseIndex, UseNpgsqlSearchCheckpoints, readiness. Do not add a Typesense SDK, decompile the package
  or search the web for it.
---
# Tarol search on Typesense

## Rules
* Register after `AddSearch`: `.UseNpgsqlSearchCheckpoints().UseTypesense(provider => client, options)`. Without an
  `ISearchCheckpoints` the start fails with a message naming `UseNpgsqlSearchCheckpoints()`.
* Build the client with `TypesenseTarolBuilderExtensions.Client(server, apiKey)`. Read the API key from configuration or a
  secret store. Never put it in code, in committed settings or in a test (tests generate a throwaway key per run).
* The collection of version n is `<CollectionPrefix>_v<n>`. Raising `s.Version` builds a new collection; the newest current
  version answers meanwhile. Delete an old collection yourself once the new one is current.
* Writes are synchronous: a search after the host's batch sees it. Read-your-writes costs the feeder's lag only.
* German documents (`[SearchLanguage]` = "de") are stemmed German and still folded; queries with `Language = "de"` search both.

## Example
```csharp
builder.Services.AddTarol(AppJson.Default, codec => codec.Add<Artwork>())
    .UseNpgsqlStore(dataSource)
    .UseNpgsqlReadModels()
    .AddSearch(s => s.Add<Artwork>())
    .UseNpgsqlSearchCheckpoints()
    .UseTypesense(_ => TypesenseTarolBuilderExtensions.Client(new Uri(configuration["Search:Url"]!), configuration["Search:ApiKey"]!),
        new TypesenseOptions { CollectionPrefix = "artflow" });
```

## Common mistakes
* No `UseNpgsqlSearchCheckpoints()` → `InvalidOperationException` at the first resolve.
* An API key in source or `appsettings.json` committed to git.
* Expecting the `de` locale to fold umlauts: it does not; Tarol keeps the folded field for that.

## AOT
HttpClient and `Utf8JsonWriter`/`JsonDocument`; 0 IL warnings (`Tarol.Search.Typesense.AotSmoke`).
