using System.Globalization;
using System.Text.Json;
using Tarol.Queries;
using Tarol.Results;

namespace Tarol.Search.OpenSearch;

/// <summary>Options of the OpenSearch index.</summary>
public sealed record OpenSearchOptions
{
    /// <summary>The index name prefix; version n lives in <c>&lt;prefix&gt;-v&lt;n&gt;</c> (lowercase).</summary>
    public string IndexPrefix { get; init; } = "tarol-search";

    /// <summary>Wait for a refresh after each write (<c>refresh=wait_for</c>): read-your-writes, at about a second per batch.</summary>
    public bool WaitForRefresh { get; init; }

    /// <summary>Primary shards of a new index.</summary>
    public int Shards { get; init; } = 1;

    /// <summary>Replicas of a new index.</summary>
    public int Replicas { get; init; } = 1;
}

/// <summary>
/// The search index in OpenSearch (GS-3): <c>&lt;prefix&gt;-v&lt;n&gt;</c> per version; tenant, type, aggregate, exact tokens and
/// audience as keywords; the text per weight analysed with folding, and in <c>.en</c>/<c>.de</c> sub-fields with English and German
/// stemming. Queries come from Tarol's parser (phrases, exclusions, OR, a prefix), ranked by BM25 with the weights as boosts. The
/// documents are written first, the checkpoint moves after (<see cref="ISearchCheckpoints"/>): at least once, harmless to repeat.
/// </summary>
public sealed class OpenSearchIndex : ISearchIndex
{
    private static readonly (string Field, double Boost)[] Weights = [("a", 10), ("b", 4), ("c", 2), ("d", 1)];
    private readonly HttpClient _http;
    private readonly ISearchCheckpoints _checkpoints;
    private readonly OpenSearchOptions _options;
    private int _version = 1;

    /// <summary>Creates the index.</summary>
    /// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the cluster (authentication configured by the app).</param>
    /// <param name="checkpoints">Where the checkpoints live (<c>UseNpgsqlSearchCheckpoints</c>).</param>
    /// <param name="options">Prefix, refresh, shards.</param>
    public OpenSearchIndex(HttpClient http, ISearchCheckpoints checkpoints, OpenSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(checkpoints);
        _http = http;
        _checkpoints = checkpoints;
        _options = options ?? new OpenSearchOptions();
        if (_options.IndexPrefix.Length == 0 || _options.IndexPrefix.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')))
            throw new ArgumentException("The index prefix is lowercase letters, digits, '-' and '_'.", nameof(options));
    }

    /// <summary>A readiness problem: the cluster did not answer the last request (<see langword="null"/> while it answers).</summary>
    public string? Problem => EngineJson.Unreachable(_http) is { } at ? $"OpenSearch did not answer since {at:O}" : null;

    /// <summary>The index of a version.</summary>
    /// <param name="version">The version.</param>
    /// <returns><c>&lt;prefix&gt;-v&lt;n&gt;</c>.</returns>
    public string IndexOf(int version) => _options.IndexPrefix + "-v" + version.ToString(CultureInfo.InvariantCulture);

    private string Refresh => _options.WaitForRefresh ? "?refresh=wait_for" : "";

    /// <inheritdoc />
    public async ValueTask Prepare(SearchIndexDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var index = IndexOf(definition.Version);
        using (var existing = await EngineJson.Send(_http, HttpMethod.Get, "/" + index + "/_mapping", null, cancellationToken, allowNotFound: true).ConfigureAwait(false))
        {
            if (existing.RootElement.TryGetProperty(index, out var mapping))
            {
                var stored = mapping.GetProperty("mappings").TryGetProperty("_meta", out var meta) && meta.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
                if (stored != definition.Fingerprint)
                    throw new InvalidOperationException(
                        $"The search index v{definition.Version} was built for other searchable members (fingerprint {stored}, now {definition.Fingerprint}): raise the version.");
            }
            else
            {
                try
                {
                    using var _ = await EngineJson.Send(_http, HttpMethod.Put, "/" + index, EngineJson.Body(w => Definition(w, definition)), cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.BadRequest && e.Message.Contains("resource_already_exists", StringComparison.Ordinal))
                {
                    // another process created it first: its fingerprint is checked on the next start
                }
            }
        }
        await _checkpoints.Prepare(definition.Version, cancellationToken).ConfigureAwait(false);
        _version = definition.Version;
    }

    private void Definition(Utf8JsonWriter w, SearchIndexDefinition definition)
    {
        w.WriteStartObject();
        w.WriteStartObject("settings");
        w.WriteNumber("number_of_shards", _options.Shards);
        w.WriteNumber("number_of_replicas", _options.Replicas);
        w.WriteStartObject("analysis");
        w.WriteStartObject("filter");
        w.WriteStartObject("tarol_en_stem"); w.WriteString("type", "stemmer"); w.WriteString("language", "english"); w.WriteEndObject();
        w.WriteStartObject("tarol_de_stem"); w.WriteString("type", "stemmer"); w.WriteString("language", "light_german"); w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartObject("analyzer");
        Analyzer(w, "tarol", "lowercase", "asciifolding");
        Analyzer(w, "tarol_en", "lowercase", "asciifolding", "tarol_en_stem");
        Analyzer(w, "tarol_de", "lowercase", "german_normalization", "asciifolding", "tarol_de_stem");
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartObject("mappings");
        w.WriteStartObject("_meta"); w.WriteString("fingerprint", definition.Fingerprint); w.WriteNumber("version", definition.Version); w.WriteEndObject();
        w.WriteString("dynamic", "strict");
        w.WriteStartObject("properties");
        foreach (var keyword in new[] { "tenant", "type", "id", "aggregate", "exact", "audience", "lang" })
        {
            w.WriteStartObject(keyword); w.WriteString("type", "keyword"); w.WriteEndObject();
        }
        w.WriteStartObject("version"); w.WriteString("type", "long"); w.WriteEndObject();
        w.WriteStartObject("title"); w.WriteString("type", "keyword"); w.WriteBoolean("index", false); w.WriteEndObject();
        foreach (var (field, _) in Weights)
        {
            w.WriteStartObject(field);
            w.WriteString("type", "text");
            w.WriteString("analyzer", "tarol");
            w.WriteStartObject("fields");
            w.WriteStartObject("en"); w.WriteString("type", "text"); w.WriteString("analyzer", "tarol_en"); w.WriteEndObject();
            w.WriteStartObject("de"); w.WriteString("type", "text"); w.WriteString("analyzer", "tarol_de"); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Analyzer(Utf8JsonWriter w, string name, params string[] filters)
    {
        w.WriteStartObject(name);
        w.WriteString("tokenizer", "standard");
        w.WriteStartArray("filter");
        foreach (var f in filters)
            w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => _checkpoints.Checkpoint(_version, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> Write(SearchBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if ((await _checkpoints.Checkpoint(_version, cancellationToken).ConfigureAwait(false)).Position != batch.From)
            return false;                                                                         // another runner moved it: write nothing
        await Apply(batch.Streams, cancellationToken).ConfigureAwait(false);
        return await _checkpoints.Move(_version, batch.From, batch.To, batch.State, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask Replace(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        await Apply(streams, cancellationToken).ConfigureAwait(false);
        using var _ = await EngineJson.Send(_http, HttpMethod.Post, "/" + IndexOf(_version) + "/_refresh", null, cancellationToken).ConfigureAwait(false);   // erasure is visible at once
    }

    private static string Key(Guid tenant, SearchDocument d) => tenant.ToString("N") + ":" + d.Type + ":" + d.Id.ToString("N");

    /// <summary>Indexes the new documents, then deletes the streams' documents that are not among them (a removed entity, a gone stream).</summary>
    private async Task Apply(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        if (streams.Count == 0)
            return;
        var index = IndexOf(_version);
        if (streams.Any(static s => s.Documents.Count > 0))
        {
            using var bulk = await EngineJson.Send(_http, HttpMethod.Post, "/_bulk" + Refresh, EngineJson.Lines(next =>
            {
                foreach (var stream in streams)
                    foreach (var d in stream.Documents)
                    {
                        using (var h = next())
                        {
                            h.WriteStartObject(); h.WriteStartObject("index"); h.WriteString("_index", index); h.WriteString("_id", Key(stream.Tenant, d));
                            h.WriteEndObject(); h.WriteEndObject();
                        }
                        using var w = next();
                        Document(w, stream, d);
                    }
            }), cancellationToken).ConfigureAwait(false);
            if (bulk.RootElement.GetProperty("errors").GetBoolean())
                throw new HttpRequestException("OpenSearch refused documents: " + bulk.RootElement.GetRawText()[..Math.Min(500, bulk.RootElement.GetRawText().Length)]);
        }
        // stale documents become visible to delete_by_query only after a refresh
        using (var _ = await EngineJson.Send(_http, HttpMethod.Post, "/" + index + "/_refresh", null, cancellationToken).ConfigureAwait(false)) { }
        foreach (var tenant in streams.GroupBy(static s => s.Tenant))
        {
            using var deleted = await EngineJson.Send(_http, HttpMethod.Post, "/" + index + "/_delete_by_query?conflicts=proceed&refresh=true", EngineJson.Body(w =>
            {
                w.WriteStartObject(); w.WriteStartObject("query"); w.WriteStartObject("bool");
                w.WriteStartArray("filter");
                Term(w, "tenant", tenant.Key.ToString("N"));
                Terms(w, "aggregate", tenant.Select(static s => s.Aggregate.ToString("N")));
                w.WriteEndArray();
                var keep = tenant.SelectMany(s => s.Documents.Select(d => Key(tenant.Key, d))).ToList();
                if (keep.Count > 0)
                {
                    w.WriteStartArray("must_not");
                    w.WriteStartObject(); w.WriteStartObject("ids"); w.WriteStartArray("values");
                    foreach (var k in keep)
                        w.WriteStringValue(k);
                    w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
                    w.WriteEndArray();
                }
                w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject();
            }), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Document(Utf8JsonWriter w, StreamDocuments stream, SearchDocument d)
    {
        w.WriteStartObject();
        w.WriteString("tenant", stream.Tenant.ToString("N"));
        w.WriteString("type", d.Type);
        w.WriteString("id", d.Id.ToString("N"));
        w.WriteString("aggregate", d.Aggregate.ToString("N"));
        w.WriteNumber("version", stream.Version);
        w.WriteString("title", d.Title);
        foreach (var (field, text) in new[] { ("a", d.A), ("b", d.B), ("c", d.C), ("d", d.D) })
            if (text is not null)
                w.WriteString(field, text);
        w.WriteStartArray("exact");
        foreach (var e in d.Exact)
            w.WriteStringValue(e);
        w.WriteEndArray();
        w.WriteStartArray("audience");
        foreach (var a in d.Audience)
            w.WriteStringValue(a);
        w.WriteEndArray();
        if (d.Language is { } lang)
            w.WriteString("lang", lang);
        w.WriteEndObject();
    }

    /// <inheritdoc />
    public async ValueTask<Result<SearchPage>> Search(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SearchText.Validate(request) is { } invalid)
            return invalid;
        var answering = await _checkpoints.NewestCurrent(_version, cancellationToken).ConfigureAwait(false);
        var version = answering?.Version ?? _version;
        var checkpoint = answering?.Checkpoint ?? await _checkpoints.Checkpoint(_version, cancellationToken).ConfigureAwait(false);
        if (SearchText.Offset(request, version, out var offset) is { } bad)
            return bad;
        var clauses = SearchText.Parse(request.Text);
        var hasText = clauses.Any(static c => !c.Excluded);
        var hits = new List<SearchHit>();
        Dictionary<string, long>? counts = request.Counts ? [] : null;
        if (hasText || request.ExactToken is not null)
        {
            using var found = await EngineJson.Send(_http, HttpMethod.Post, "/" + IndexOf(version) + "/_search", EngineJson.Body(w =>
                Query(w, request, clauses, offset, request.Limit + 1, typo: false)), cancellationToken).ConfigureAwait(false);
            Read(found.RootElement, hits, counts, request, null);
            if (hits.Count == 0 && offset == 0 && hasText)
            {
                using var fuzzy = await EngineJson.Send(_http, HttpMethod.Post, "/" + IndexOf(version) + "/_search", EngineJson.Body(w =>
                    Query(w, request, clauses, 0, 200, typo: true)), cancellationToken).ConfigureAwait(false);
                Read(fuzzy.RootElement, hits, counts is null ? null : (counts = []), request, clauses);
            }
        }
        var more = hits.Count > request.Limit;
        if (more)
            hits.RemoveRange(request.Limit, hits.Count - request.Limit);
        var next = more ? SearchText.Cursor(request, version, offset + hits.Count) : null;
        return new SearchPage(hits, next, counts, false, version, checkpoint.Position, checkpoint.State);
    }

    private static void Read(JsonElement root, List<SearchHit> hits, Dictionary<string, long>? counts, SearchRequest request, IReadOnlyList<SearchClause>? typo)
    {
        foreach (var h in root.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = h.GetProperty("_source");
            if (typo is not null)
            {
                var words = SearchText.Words(source.TryGetProperty("a", out var a) ? a.GetString() : null);
                if (!typo.Where(static c => !c.Excluded).All(c => c.Alternatives.Any(alt => SearchText.TypoMatch(alt, words))))
                    continue;
                if (hits.Count > request.Limit)
                    continue;
            }
            string? snippet = null;
            if (request.Snippets && h.TryGetProperty("highlight", out var highlight))
                foreach (var field in highlight.EnumerateObject())
                {
                    snippet = field.Value[0].GetString();
                    break;
                }
            hits.Add(new SearchHit(source.GetProperty("type").GetString()!, Guid.ParseExact(source.GetProperty("id").GetString()!, "N"),
                Guid.ParseExact(source.GetProperty("aggregate").GetString()!, "N"), source.GetProperty("title").GetString()!,
                h.GetProperty("_score").ValueKind == JsonValueKind.Number ? h.GetProperty("_score").GetDouble() : 0, snippet));
            if (typo is not null && counts is not null)
                counts[hits[^1].Type] = counts.GetValueOrDefault(hits[^1].Type) + 1;
        }
        if (typo is null && counts is not null && root.TryGetProperty("aggregations", out var aggregations))
            foreach (var bucket in aggregations.GetProperty("types").GetProperty("buckets").EnumerateArray())
                counts[bucket.GetProperty("key").GetString()!] = bucket.GetProperty("doc_count").GetInt64();
    }

    private static string[] Fields(SearchRequest request, bool typo)
    {
        if (typo)
            return ["a"];
        var languages = request.Language?.ToUpperInvariant() switch
        {
            "EN" => new[] { "", ".en" },
            "DE" => ["", ".de"],
            _ => ["", ".en", ".de"],
        };
        return [.. Weights.SelectMany(w => languages.Select(l => w.Field + l + "^" + w.Boost.ToString(CultureInfo.InvariantCulture)))];
    }

    private static void Query(Utf8JsonWriter w, SearchRequest request, IReadOnlyList<SearchClause> clauses, int from, int size, bool typo)
    {
        w.WriteStartObject();
        w.WriteNumber("from", from);
        w.WriteNumber("size", size);
        w.WriteBoolean("track_total_hits", false);
        w.WriteStartArray("sort");
        w.WriteStringValue("_score");
        w.WriteStartObject(); w.WriteString("id", "asc"); w.WriteEndObject();
        w.WriteEndArray();
        w.WriteStartObject("query"); w.WriteStartObject("bool");
        w.WriteStartArray("filter");
        Term(w, "tenant", request.Tenant.ToString("N"));
        if (request.Types is { } types)
            Terms(w, "type", types);
        if (request.Audience is { } audience)
        {
            w.WriteStartObject(); w.WriteStartObject("bool"); w.WriteNumber("minimum_should_match", 1); w.WriteStartArray("should");
            w.WriteStartObject(); w.WriteStartObject("bool"); w.WriteStartArray("must_not");
            w.WriteStartObject(); w.WriteStartObject("exists"); w.WriteString("field", "audience"); w.WriteEndObject(); w.WriteEndObject();
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
            Terms(w, "audience", audience);
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
        }
        w.WriteEndArray();
        // the text (or the exact token): must match one of them
        w.WriteNumber("minimum_should_match", 1);
        w.WriteStartArray("should");
        var positives = clauses.Where(static c => !c.Excluded).ToList();
        if (positives.Count > 0)
        {
            w.WriteStartObject(); w.WriteStartObject("bool");
            w.WriteStartArray("must");
            var last = request.Prefix && !typo ? positives[^1] : null;
            foreach (var clause in positives)
            {
                w.WriteStartObject(); w.WriteStartObject("bool"); w.WriteNumber("minimum_should_match", 1); w.WriteStartArray("should");
                foreach (var alternative in clause.Alternatives)
                    Match(w, string.Join(' ', alternative), Fields(request, typo),
                        typo ? "best_fields" : ReferenceEquals(clause, last) && ReferenceEquals(alternative, clause.Alternatives[^1]) ? "phrase_prefix" : "phrase", typo);
                w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
            }
            w.WriteEndArray();
            if (!typo && clauses.Any(static c => c.Excluded))
            {
                w.WriteStartArray("must_not");
                foreach (var clause in clauses.Where(static c => c.Excluded))
                    foreach (var alternative in clause.Alternatives)
                        Match(w, string.Join(' ', alternative), Fields(request, false), "phrase", false);
                w.WriteEndArray();
            }
            w.WriteEndObject(); w.WriteEndObject();
        }
        if (request.ExactToken is { } token && !typo)
        {
            w.WriteStartObject(); w.WriteStartObject("constant_score");
            w.WriteStartObject("filter"); w.WriteStartObject("term"); w.WriteString("exact", token); w.WriteEndObject(); w.WriteEndObject();
            w.WriteNumber("boost", 50); w.WriteEndObject(); w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject(); w.WriteEndObject();
        if (request.Counts && !typo)
        {
            w.WriteStartObject("aggs"); w.WriteStartObject("types"); w.WriteStartObject("terms"); w.WriteString("field", "type"); w.WriteNumber("size", 200);
            w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject();
        }
        if (request.Snippets && !typo)
        {
            w.WriteStartObject("highlight");
            w.WriteStartArray("pre_tags"); w.WriteStringValue("<b>"); w.WriteEndArray();
            w.WriteStartArray("post_tags"); w.WriteStringValue("</b>"); w.WriteEndArray();
            w.WriteBoolean("require_field_match", false);
            w.WriteStartObject("fields");
            foreach (var field in new[] { "b", "b.en", "b.de", "a", "a.en", "a.de" })
            {
                w.WriteStartObject(field); w.WriteNumber("fragment_size", 80); w.WriteNumber("number_of_fragments", 1); w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void Match(Utf8JsonWriter w, string text, string[] fields, string type, bool fuzzy)
    {
        w.WriteStartObject(); w.WriteStartObject("multi_match");
        w.WriteString("query", text);
        w.WriteString("type", type);
        w.WriteStartArray("fields");
        foreach (var f in fields)
            w.WriteStringValue(f);
        w.WriteEndArray();
        if (fuzzy)
        {
            w.WriteString("fuzziness", "1");
            w.WriteString("operator", "and");
        }
        w.WriteEndObject(); w.WriteEndObject();
    }

    private static void Term(Utf8JsonWriter w, string field, string value)
    {
        w.WriteStartObject(); w.WriteStartObject("term"); w.WriteString(field, value); w.WriteEndObject(); w.WriteEndObject();
    }

    private static void Terms(Utf8JsonWriter w, string field, IEnumerable<string> values)
    {
        w.WriteStartObject(); w.WriteStartObject("terms"); w.WriteStartArray(field);
        foreach (var v in values)
            w.WriteStringValue(v);
        w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SearchHit>> Documents(Guid tenant, IReadOnlyCollection<Guid> aggregates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        using var found = await EngineJson.Send(_http, HttpMethod.Post, "/" + IndexOf(_version) + "/_search", EngineJson.Body(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("size", SearchLimits.MaxDepth);
            w.WriteStartObject("query"); w.WriteStartObject("bool"); w.WriteStartArray("filter");
            Term(w, "tenant", tenant.ToString("N"));
            Terms(w, "aggregate", aggregates.Select(static a => a.ToString("N")));
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
            w.WriteEndObject();
        }), cancellationToken).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        Read(found.RootElement, hits, null, new SearchRequest(tenant, "") { Limit = SearchLimits.MaxLimit }, null);
        return hits;
    }
}
