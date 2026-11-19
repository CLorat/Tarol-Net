using Tarol.Persistence;
using Tarol.Queries;
using Tarol.Results;

namespace Tarol.Search;

/// <summary>One index version as the host prepares it: the version, the fingerprint of every shape and the document types.</summary>
/// <param name="Version">The index version (GS-5).</param>
/// <param name="Fingerprint">The combined fingerprint of the registered shapes; an index refuses a stored version whose fingerprint differs.</param>
/// <param name="Types">Every document type of the registered aggregates.</param>
public sealed record SearchIndexDefinition(int Version, string Fingerprint, IReadOnlyList<SearchType> Types)
{
    /// <summary>The checkpoint name of the index (a row of <c>tarol.projections</c> in PostgreSQL).</summary>
    public const string Name = "search";
}

/// <summary>The documents of one stream after a change: they replace the stream's stored documents (none = remove them).</summary>
/// <param name="Tenant">The tenant (<see cref="Guid.Empty"/> for none).</param>
/// <param name="Aggregate">The stream id.</param>
/// <param name="Version">The stream version the documents reflect (0 when the stream is gone).</param>
/// <param name="Documents">Its documents, exact values already blinded.</param>
public sealed record StreamDocuments(Guid Tenant, Guid Aggregate, long Version, IReadOnlyList<SearchDocument> Documents);

/// <summary>One write of the host: the streams' documents and the checkpoint move, together or not at all (an engine: the documents first).</summary>
/// <param name="Streams">The streams that changed in the batch.</param>
/// <param name="From">The checkpoint the batch read after; the write is refused when the stored one differs (another runner).</param>
/// <param name="To">The position of the batch's last event.</param>
/// <param name="State">The state after the batch.</param>
public sealed record SearchBatch(IReadOnlyList<StreamDocuments> Streams, GlobalPosition From, GlobalPosition To, ProjectionState State);

/// <summary>A search as an index runs it: the tenant first, then the text and the filters the caller may use.</summary>
/// <param name="Tenant">The tenant (<see cref="Guid.Empty"/> for none); never from the request.</param>
/// <param name="Text">The query in web-search syntax: words (all must match), <c>"a phrase"</c>, <c>-word</c>, <c>OR</c>.</param>
public sealed record SearchRequest(Guid Tenant, string Text)
{
    /// <summary>The document types the caller may see and asked for; <see langword="null"/> = every type (system callers only).</summary>
    public IReadOnlyCollection<string>? Types { get; init; }

    /// <summary>The caller's audience keys (subject, roles); <see langword="null"/> = no audience filter (system callers only).</summary>
    public IReadOnlyCollection<string>? Audience { get; init; }

    /// <summary>The last word is a prefix (search as you type).</summary>
    public bool Prefix { get; init; }

    /// <summary>Count the matches per type (up to <see cref="SearchLimits.CountCap"/>).</summary>
    public bool Counts { get; init; }

    /// <summary>Return a plain-text snippet with the matched words as <see cref="SearchHit.Highlights"/>.</summary>
    public bool Snippets { get; init; }

    /// <summary>The page size (1 … <see cref="SearchLimits.MaxLimit"/>).</summary>
    public int Limit { get; init; } = SearchLimits.DefaultLimit;

    /// <summary>The cursor of the previous page (<see cref="SearchPage.Next"/>), or <see langword="null"/> for the first.</summary>
    public string? After { get; init; }

    /// <summary>The query's language (ISO 639-1); <see langword="null"/> = the simple configuration.</summary>
    public string? Language { get; init; }

    /// <summary>The blind token of the whole normalised text: finds documents whose exact values equal it (set by <see cref="ISearch"/>).</summary>
    public string? ExactToken { get; init; }
}

/// <summary>The limits every index applies.</summary>
public static class SearchLimits
{
    /// <summary>The default page size.</summary>
    public const int DefaultLimit = 20;

    /// <summary>The largest page.</summary>
    public const int MaxLimit = 50;

    /// <summary>The deepest hit a cursor reaches, and the number of matches an index ranks (GS-2, GS-9).</summary>
    public const int MaxDepth = 1000;

    /// <summary>Counts per type stop here; beyond it a page says <see cref="SearchPage.CountsCapped"/>.</summary>
    public const int CountCap = 1000;

    /// <summary>The longest query text.</summary>
    public const int MaxText = 256;
}

/// <summary>A hit: the document's type and id, the aggregate it is shown through, its title, score and snippet.</summary>
/// <param name="Type">The document type.</param>
/// <param name="Id">The document id (an aggregate's or an entity's).</param>
/// <param name="Aggregate">The aggregate's id.</param>
/// <param name="Title">The display title.</param>
/// <param name="Score">The rank (higher first; comparable within one page only).</param>
/// <param name="Snippet">The snippet as plain text, when asked for; the matched words are in <see cref="Highlights"/> (BC-8: never
/// markup, so a client shows it as text).</param>
public sealed record SearchHit(string Type, Guid Id, Guid Aggregate, string Title, double Score, string? Snippet = null)
{
    /// <summary>The matched words of <see cref="Snippet"/> as UTF-16 offsets (a JavaScript string's indexes), in order and
    /// without overlap; empty without a snippet.</summary>
    public IReadOnlyList<SearchHighlight> Highlights { get; init; } = [];
}

/// <summary>A highlighted range of a <see cref="SearchHit.Snippet"/>.</summary>
/// <param name="Start">The first UTF-16 code unit.</param>
/// <param name="Length">The number of UTF-16 code units.</param>
public readonly record struct SearchHighlight(int Start, int Length);

/// <summary>
/// Snippets for <see cref="ISearchIndex"/> implementations: an engine marks the matched words with <see cref="Open"/> and
/// <see cref="Close"/> (two private-use characters no text needs), and <see cref="Parse"/> turns its answer into plain text and
/// <see cref="SearchHighlight"/> offsets. Markup in the indexed text stays text.
/// </summary>
public static class SearchSnippets
{
    /// <summary>The start marker an engine puts before a matched word (U+E000).</summary>
    public const string Open = "\uE000";

    /// <summary>The end marker an engine puts after a matched word (U+E001).</summary>
    public const string Close = "\uE001";

    /// <summary>Splits an engine's marked snippet into text and highlights. Unbalanced or nested markers are dropped, empty
    /// ranges are skipped, adjacent ranges merge.</summary>
    /// <param name="marked">The snippet with <see cref="Open"/>/<see cref="Close"/> markers; <see langword="null"/> for none.</param>
    /// <returns>The text (<see langword="null"/> for none) and the highlights.</returns>
    public static (string? Text, IReadOnlyList<SearchHighlight> Highlights) Parse(string? marked)
    {
        if (marked is null)
            return (null, []);
        var text = new System.Text.StringBuilder(marked.Length);
        var highlights = new List<SearchHighlight>();
        var start = -1;
        foreach (var c in marked)
        {
            if (c == Open[0])
                start = text.Length;                                             // a second open restarts the range
            else if (c == Close[0])
            {
                if (start >= 0 && text.Length > start)
                {
                    if (highlights.Count > 0 && highlights[^1].Start + highlights[^1].Length == start)
                        highlights[^1] = highlights[^1] with { Length = highlights[^1].Length + text.Length - start };
                    else
                        highlights.Add(new SearchHighlight(start, text.Length - start));
                }
                start = -1;
            }
            else
                text.Append(c);
        }
        return (text.ToString(), highlights);
    }

    /// <summary>A hit with its snippet parsed by <see cref="Parse"/>.</summary>
    /// <param name="type">The document type.</param>
    /// <param name="id">The document id.</param>
    /// <param name="aggregate">The aggregate's id.</param>
    /// <param name="title">The display title.</param>
    /// <param name="score">The rank.</param>
    /// <param name="marked">The engine's marked snippet, or <see langword="null"/>.</param>
    /// <returns>The hit.</returns>
    public static SearchHit Hit(string type, Guid id, Guid aggregate, string title, double score, string? marked)
    {
        var (text, highlights) = Parse(marked);
        return new SearchHit(type, id, aggregate, title, score, text) { Highlights = highlights };
    }
}

/// <summary>A page of hits.</summary>
/// <param name="Items">The hits, best first.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> at the end (or at <see cref="SearchLimits.MaxDepth"/>).</param>
/// <param name="Counts">Matches per type, when asked for.</param>
/// <param name="CountsCapped">The counts stopped at <see cref="SearchLimits.CountCap"/> matches.</param>
/// <param name="Version">The index version that answered (the previous one while a new version builds, GS-5).</param>
/// <param name="Position">That version's checkpoint (the <c>Tarol-Position</c> of the answer).</param>
/// <param name="State">That version's state: <see cref="ProjectionState.Building"/> only when no version is current yet (its hits are incomplete; HTTP answers 503).</param>
public sealed record SearchPage(IReadOnlyList<SearchHit> Items, string? Next, IReadOnlyDictionary<string, long>? Counts, bool CountsCapped, int Version,
    GlobalPosition Position, ProjectionState State);

/// <summary>
/// Where the documents of one application's search live: <see cref="InMemorySearchIndex"/>, PostgreSQL (Tarol.Search.Npgsql),
/// OpenSearch, Typesense. Every read and write is tenant-scoped; the contract suite in the repository checks each implementation.
/// </summary>
public interface ISearchIndex
{
    /// <summary>Creates the version's storage if it does not exist; refuses a stored version whose fingerprint differs.</summary>
    /// <param name="definition">The version, the fingerprint and the types.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">The version exists with another fingerprint: raise the version (GS-5).</exception>
    ValueTask Prepare(SearchIndexDefinition definition, CancellationToken cancellationToken);

    /// <summary>The version's stored checkpoint (<see cref="GlobalPosition.Start"/> and building before the first batch).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checkpoint.</returns>
    ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken);

    /// <summary>Replaces the streams' documents and moves the checkpoint (guarded: it must still be at <see cref="SearchBatch.From"/>).</summary>
    /// <param name="batch">The batch.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="false"/> when another runner moved the checkpoint.</returns>
    ValueTask<bool> Write(SearchBatch batch, CancellationToken cancellationToken);

    /// <summary>Replaces the streams' documents without moving the checkpoint (erasure, retention).</summary>
    /// <param name="streams">The streams.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    ValueTask Replace(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken);

    /// <summary>A page of hits.</summary>
    /// <param name="request">The search.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page, or <see cref="Invalid"/> for a bad limit, text or cursor (HTTP 400).</returns>
    ValueTask<Result<SearchPage>> Search(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>The stored documents of some aggregates (an export lists what the index holds of a subject).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="aggregates">The aggregates' ids.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The documents as hits (score 0).</returns>
    ValueTask<IReadOnlyList<SearchHit>> Documents(Guid tenant, IReadOnlyCollection<Guid> aggregates, CancellationToken cancellationToken);
}
