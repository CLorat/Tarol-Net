namespace Probe.Search;

/// <summary>How the text of a query is matched.</summary>
public enum Match
{
    /// <summary>All words (AND), stemmed; the engine's default ranking.</summary>
    Words,

    /// <summary>The last word is a prefix ("kave" finds "kaveri").</summary>
    Prefix,

    /// <summary>A misspelt word should still match (edit distance 1).</summary>
    Typo,
}

/// <summary>A search: text, tenant (null = across tenants), types, page size, counts per type.</summary>
public sealed record Query(string Text, Guid? Tenant, Match Match = Match.Words, int Limit = 20, bool Facets = false, string[]? Types = null,
    bool Snippets = false);

/// <summary>A hit: the document id, its type and the engine's score.</summary>
public readonly record struct Hit(Guid Id, string Type, double Score);

/// <summary>A page of hits, the engine's total (exact or estimated) and the counts per type when asked.</summary>
public sealed record Hits(IReadOnlyList<Hit> Items, long Total, IReadOnlyDictionary<string, long>? PerType = null, string? Snippet = null);

/// <summary>What the probe asks of every candidate: load (bulk), upsert/delete one document, search, size.</summary>
public interface IEngine : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Drops and recreates the index (empty).</summary>
    Task Reset();

    /// <summary>Bulk-loads documents and returns once they are searchable.</summary>
    Task Load(IEnumerable<Doc> docs);

    /// <summary>Writes one document (as a feeder would) without waiting for it to become searchable.</summary>
    Task Upsert(Doc doc);

    /// <summary>Deletes documents by id without waiting.</summary>
    Task Delete(IReadOnlyCollection<Guid> ids);

    Task<Hits> Search(Query query);

    /// <summary>Index size in bytes (on disk, or what the engine reports).</summary>
    Task<long> Size();
}
