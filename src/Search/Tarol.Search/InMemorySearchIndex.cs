using System.Text;
using Tarol.Queries;
using Tarol.Results;

namespace Tarol.Search;

/// <summary>
/// The search index in memory, for tests (<see cref="SearchTest"/>) and the contract suite: words, phrases, exclusions, OR, a
/// prefix on the last word, a typo fallback (one edit on the words of weight A: titles and names) when nothing matches, counts per type, snippets, the
/// cursor. Ranking weighs A 1.0, B 0.4, C 0.2, D 0.1 per matched word. Thread-safe; one version.
/// </summary>
public sealed class InMemorySearchIndex : ISearchIndex
{
    private static readonly double[] Weights = [1.0, 0.4, 0.2, 0.1];
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Tenant, string Type, Guid Id), Entry> _documents = [];
    private SearchIndexDefinition? _definition;
    private ProjectionCheckpoint _checkpoint;

    private sealed record Entry(Guid Tenant, SearchDocument Document, IReadOnlyList<string>[] Fields);

    /// <summary>The stored documents (a copy), for assertions.</summary>
    public IReadOnlyList<(Guid Tenant, SearchDocument Document)> All
    {
        get
        {
            lock (_gate)
                return [.. _documents.Values.Select(static e => (e.Tenant, e.Document))];
        }
    }

    /// <inheritdoc />
    public ValueTask Prepare(SearchIndexDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (_definition is { } stored && stored.Version == definition.Version && stored.Fingerprint != definition.Fingerprint)
                throw new InvalidOperationException(
                    $"The search index v{definition.Version} was built for other searchable members (fingerprint {stored.Fingerprint}, now {definition.Fingerprint}): raise the version.");
            _definition = definition;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_checkpoint);
    }

    /// <inheritdoc />
    public ValueTask<bool> Write(SearchBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_checkpoint.Position != batch.From)
                return ValueTask.FromResult(false);
            Apply(batch.Streams);
            _checkpoint = new ProjectionCheckpoint(batch.To, batch.State);
        }
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask Replace(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        lock (_gate)
            Apply(streams);
        return ValueTask.CompletedTask;
    }

    private void Apply(IReadOnlyList<StreamDocuments> streams)
    {
        foreach (var stream in streams)
        {
            foreach (var key in _documents.Keys.Where(k => k.Tenant == stream.Tenant && _documents[k].Document.Aggregate == stream.Aggregate).ToList())
                _documents.Remove(key);
            foreach (var d in stream.Documents)
                _documents[(stream.Tenant, d.Type, d.Id)] = new Entry(stream.Tenant, d,
                    [SearchText.Words(d.A), SearchText.Words(d.B), SearchText.Words(d.C), SearchText.Words(d.D)]);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<SearchPage>> Search(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SearchText.Validate(request) is { } invalid)
            return ValueTask.FromResult<Result<SearchPage>>(invalid);
        int version;
        ProjectionCheckpoint checkpoint;
        List<Entry> candidates;
        lock (_gate)
        {
            version = _definition?.Version ?? 1;
            checkpoint = _checkpoint;
            candidates = [.. _documents.Values.Where(e => e.Tenant == request.Tenant
                && (request.Types is null || request.Types.Contains(e.Document.Type))
                && (request.Audience is null || e.Document.Audience.Count == 0 || e.Document.Audience.Any(request.Audience.Contains)))];
        }
        if (SearchText.Offset(request, version, out var offset) is { } bad)
            return ValueTask.FromResult<Result<SearchPage>>(bad);

        var clauses = SearchText.Parse(request.Text);
        var scored = new List<(Entry Entry, double Score)>();
        if (clauses.Any(static c => !c.Excluded) || request.ExactToken is not null)
        {
            foreach (var e in candidates)
                if (Score(e, clauses, request) is { } score)
                    scored.Add((e, score));
            if (scored.Count == 0 && clauses.Count > 0)
                foreach (var e in candidates)
                    if (Typo(e, clauses) is { } score)
                        scored.Add((e, score));
        }
        scored.Sort(static (x, y) => y.Score != x.Score ? y.Score.CompareTo(x.Score) : x.Entry.Document.Id.CompareTo(y.Entry.Document.Id));
        var ranked = scored.Take(SearchLimits.MaxDepth).ToList();
        var page = ranked.Skip(offset).Take(request.Limit)
            .Select(s => SearchSnippets.Hit(s.Entry.Document.Type, s.Entry.Document.Id, s.Entry.Document.Aggregate, s.Entry.Document.Title, s.Score,
                request.Snippets ? Snippet(s.Entry.Document, clauses) : null))
            .ToList();
        var next = offset + page.Count < ranked.Count ? SearchText.Cursor(request, version, offset + page.Count) : null;
        Dictionary<string, long>? counts = null;
        if (request.Counts)
        {
            counts = [];
            foreach (var (entry, _) in scored.Take(SearchLimits.CountCap))
                counts[entry.Document.Type] = counts.GetValueOrDefault(entry.Document.Type) + 1;
        }
        return ValueTask.FromResult<Result<SearchPage>>(new SearchPage(page, next, counts, request.Counts && scored.Count > SearchLimits.CountCap, version, checkpoint.Position,
            checkpoint.State));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SearchHit>> Documents(Guid tenant, IReadOnlyCollection<Guid> aggregates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<SearchHit>>([.. _documents.Values
                .Where(e => e.Tenant == tenant && aggregates.Contains(e.Document.Aggregate))
                .Select(static e => new SearchHit(e.Document.Type, e.Document.Id, e.Document.Aggregate, e.Document.Title, 0))]);
    }

    private static double? Score(Entry e, IReadOnlyList<SearchClause> clauses, SearchRequest request)
    {
        var exact = request.ExactToken is { } token && e.Document.Exact.Contains(token);
        double score = 0;
        var positive = false;
        var lastPositive = clauses.LastOrDefault(static c => !c.Excluded);
        foreach (var clause in clauses)
        {
            var prefix = request.Prefix && ReferenceEquals(clause, lastPositive);
            double best = 0;
            foreach (var words in clause.Alternatives)
                best = Math.Max(best, Matches(e.Fields, words, prefix));
            if (clause.Excluded)
            {
                if (best > 0)
                    return null;
                continue;
            }
            positive = true;
            if (best == 0)
            {
                if (!exact)
                    return null;
                positive = false;
                break;
            }
            score += best;
        }
        return exact ? score + 2.0 : positive ? score : null;
    }

    /// <summary>The weighted count of the places where the words occur adjacent (the last as a prefix when asked).</summary>
    private static double Matches(IReadOnlyList<string>[] fields, IReadOnlyList<string> words, bool prefix)
    {
        double score = 0;
        for (var f = 0; f < fields.Length; f++)
        {
            var field = fields[f];
            for (var i = 0; i + words.Count <= field.Count; i++)
            {
                var all = true;
                for (var w = 0; w < words.Count && all; w++)
                {
                    var last = w == words.Count - 1;
                    all = prefix && last ? field[i + w].StartsWith(words[w], StringComparison.Ordinal) : Same(field[i + w], words[w]);
                }
                if (all)
                    score += Weights[f];
            }
        }
        return score;
    }

    // a light stemming: a plural or verb "s" matches its singular ("paintings" ↔ "painting")
    private static bool Same(string a, string b)
        => a == b || (a.Length > 3 && a.Length == b.Length + 1 && a[^1] == 's' && a.StartsWith(b, StringComparison.Ordinal))
                  || (b.Length > 3 && b.Length == a.Length + 1 && b[^1] == 's' && b.StartsWith(a, StringComparison.Ordinal));

    private static double? Typo(Entry e, IReadOnlyList<SearchClause> clauses)
    {
        var words = e.Fields[0];   // weight A: the document's own titles and names (an entity's display title is its aggregate's)
        foreach (var clause in clauses.Where(static c => !c.Excluded))
            if (!clause.Alternatives.Any(alternative => SearchText.TypoMatch(alternative, words)))
                return null;
        return 0.1;
    }

    private static string? Snippet(SearchDocument d, IReadOnlyList<SearchClause> clauses)
    {
        var text = d.B ?? d.A ?? d.C ?? d.D;
        if (text is null)
            return null;
        var wanted = clauses.Where(static c => !c.Excluded).SelectMany(static c => c.Alternatives).SelectMany(static w => w).ToHashSet(StringComparer.Ordinal);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = Array.FindIndex(parts, p => SearchText.Words(p).Any(w => wanted.Any(q => Same(w, q) || w.StartsWith(q, StringComparison.Ordinal))));
        var from = Math.Max(0, (first < 0 ? 0 : first) - 4);
        var sb = new StringBuilder();
        foreach (var p in parts.Skip(from).Take(12))
        {
            if (sb.Length > 0)
                sb.Append(' ');
            var hit = SearchText.Words(p).Any(w => wanted.Any(q => Same(w, q) || w.StartsWith(q, StringComparison.Ordinal)));
            sb.Append(hit ? SearchSnippets.Open + p + SearchSnippets.Close : p);
        }
        return sb.ToString();
    }
}
