using System.Globalization;
using System.Text;
using System.Text.Json;
using Tarol.Queries;
using Tarol.Results;

namespace Tarol.Search.Typesense;

/// <summary>Options of the Typesense index.</summary>
public sealed record TypesenseOptions
{
    /// <summary>The collection name prefix; version n lives in <c>&lt;prefix&gt;_v&lt;n&gt;</c>, the fingerprints in <c>&lt;prefix&gt;_versions</c>.</summary>
    public string CollectionPrefix { get; init; } = "tarol_search";
}

/// <summary>
/// The search index in Typesense (GS-3): a collection per version with tenant, type, aggregate, exact tokens and audience as
/// filter fields, the text per weight stemmed English (and German in <c>*_de</c> fields). Queries come from Tarol's parser:
/// a positive clause with alternatives (<c>OR</c>) and the exact token run as one <c>multi_search</c>, merged by score. Imports
/// are synchronous: a written document is searchable at once. The checkpoint moves after the documents (<see cref="ISearchCheckpoints"/>).
/// </summary>
public sealed class TypesenseIndex : ISearchIndex
{
    private const int PerPage = 250;
    private const int MaxCombinations = 8;
    private static readonly string[] Weights = ["a", "b", "c", "d"];
    private readonly HttpClient _http;
    private readonly ISearchCheckpoints _checkpoints;
    private readonly TypesenseOptions _options;
    private int _version = 1;

    /// <summary>Creates the index.</summary>
    /// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the server and which sends <c>X-TYPESENSE-API-KEY</c> (from configuration).</param>
    /// <param name="checkpoints">Where the checkpoints live (<c>UseNpgsqlSearchCheckpoints</c>).</param>
    /// <param name="options">The collection prefix.</param>
    public TypesenseIndex(HttpClient http, ISearchCheckpoints checkpoints, TypesenseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(checkpoints);
        _http = http;
        _checkpoints = checkpoints;
        _options = options ?? new TypesenseOptions();
        if (_options.CollectionPrefix.Length == 0 || _options.CollectionPrefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("The collection prefix is letters, digits, '-' and '_'.", nameof(options));
    }

    /// <summary>The collection of a version.</summary>
    /// <param name="version">The version.</param>
    /// <returns><c>&lt;prefix&gt;_v&lt;n&gt;</c>.</returns>
    public string CollectionOf(int version) => _options.CollectionPrefix + "_v" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>A readiness problem: the server did not answer the last request (<see langword="null"/> while it answers).</summary>
    public string? Problem => EngineJson.Unreachable(_http) is { } at ? $"Typesense did not answer since {at:O}" : null;

    private string Versions => _options.CollectionPrefix + "_versions";

    /// <inheritdoc />
    public async ValueTask Prepare(SearchIndexDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await Ensure(Versions, w =>
        {
            w.WriteStartObject();
            w.WriteString("name", "fingerprint");
            w.WriteString("type", "string");
            w.WriteBoolean("index", false);
            w.WriteBoolean("optional", true);
            w.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        var key = definition.Version.ToString(CultureInfo.InvariantCulture);
        using (var import = await EngineJson.Send(_http, HttpMethod.Post, $"/collections/{Versions}/documents?action=create", EngineJson.Body(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", key);
            w.WriteString("fingerprint", definition.Fingerprint);
            w.WriteEndObject();
        }), cancellationToken, allowNotFound: false, allowConflict: true).ConfigureAwait(false))
        { }
        using (var stored = await EngineJson.Send(_http, HttpMethod.Get, $"/collections/{Versions}/documents/{key}", null, cancellationToken).ConfigureAwait(false))
        {
            var fingerprint = stored.RootElement.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
            if (fingerprint != definition.Fingerprint)
                throw new InvalidOperationException(
                    $"The search index v{definition.Version} was built for other searchable members (fingerprint {fingerprint}, now {definition.Fingerprint}): raise the version.");
        }
        await Ensure(CollectionOf(definition.Version), Fields, cancellationToken).ConfigureAwait(false);
        await _checkpoints.Prepare(definition.Version, cancellationToken).ConfigureAwait(false);
        _version = definition.Version;
    }

    private async Task Ensure(string name, Action<Utf8JsonWriter> fields, CancellationToken cancellationToken)
    {
        using (var existing = await EngineJson.Send(_http, HttpMethod.Get, "/collections/" + name, null, cancellationToken, allowNotFound: true).ConfigureAwait(false))
            if (existing.RootElement.TryGetProperty("name", out _))
                return;
        using var created = await EngineJson.Send(_http, HttpMethod.Post, "/collections", EngineJson.Body(w =>
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteStartArray("fields");
            fields(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }), cancellationToken, allowConflict: true).ConfigureAwait(false);   // another process may create it first
    }

    private static void Fields(Utf8JsonWriter w)
    {
        static void Field(Utf8JsonWriter w, string name, string type, bool facet = false, bool optional = false, bool index = true, bool sort = false,
            string? locale = null, bool stem = false)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("type", type);
            if (facet)
                w.WriteBoolean("facet", true);
            if (optional)
                w.WriteBoolean("optional", true);
            if (!index)
                w.WriteBoolean("index", false);
            if (sort)
                w.WriteBoolean("sort", true);
            if (locale is not null)
                w.WriteString("locale", locale);
            if (stem)
                w.WriteBoolean("stem", true);
            w.WriteEndObject();
        }
        Field(w, "tenant", "string");
        Field(w, "type", "string", facet: true);
        Field(w, "doc", "string", sort: true);
        Field(w, "aggregate", "string");
        Field(w, "version", "int64", index: false, optional: true);
        Field(w, "title", "string", index: false);
        foreach (var weight in Weights)
        {
            Field(w, weight, "string", optional: true, stem: true);
            Field(w, weight + "_de", "string", optional: true, locale: "de", stem: true);
        }
        Field(w, "exact", "string[]", optional: true);
        Field(w, "audience", "string[]", optional: true);
        Field(w, "open", "bool");
    }

    /// <inheritdoc />
    public ValueTask<ProjectionCheckpoint> Checkpoint(CancellationToken cancellationToken) => _checkpoints.Checkpoint(_version, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> Write(SearchBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if ((await _checkpoints.Checkpoint(_version, cancellationToken).ConfigureAwait(false)).Position != batch.From)
            return false;
        await Apply(batch.Streams, cancellationToken).ConfigureAwait(false);
        return await _checkpoints.Move(_version, batch.From, batch.To, batch.State, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask Replace(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        await Apply(streams, cancellationToken).ConfigureAwait(false);
    }

    private static string Key(Guid tenant, SearchDocument d) => tenant.ToString("N") + "-" + d.Type.Replace('.', '_') + "-" + d.Id.ToString("N");

    private static string Quoted(string value) => "`" + value.Replace("`", "", StringComparison.Ordinal) + "`";

    private async Task Apply(IReadOnlyList<StreamDocuments> streams, CancellationToken cancellationToken)
    {
        if (streams.Count == 0)
            return;
        var collection = CollectionOf(_version);
        if (streams.Any(static s => s.Documents.Count > 0))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/collections/{collection}/documents/import?action=upsert")
            {
                Content = EngineJson.Lines(next =>
                {
                    foreach (var stream in streams)
                        foreach (var d in stream.Documents)
                        {
                            using var w = next();
                            Document(w, stream, d);
                        }
                }),
            };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || text.Contains("\"success\":false", StringComparison.Ordinal))
                throw new HttpRequestException("Typesense refused documents: " + text[..Math.Min(500, text.Length)]);
        }
        foreach (var tenant in streams.GroupBy(static s => s.Tenant))
        {
            var keep = tenant.SelectMany(s => s.Documents.Select(d => Key(tenant.Key, d))).ToList();
            var filter = $"tenant:={Quoted(tenant.Key.ToString("N"))} && aggregate:=[{string.Join(',', tenant.Select(static s => Quoted(s.Aggregate.ToString("N"))))}]"
                         + (keep.Count > 0 ? $" && id:!=[{string.Join(',', keep.Select(Quoted))}]" : "");
            using var _ = await EngineJson.Send(_http, HttpMethod.Delete, $"/collections/{collection}/documents?filter_by={Uri.EscapeDataString(filter)}", null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Document(Utf8JsonWriter w, StreamDocuments stream, SearchDocument d)
    {
        w.WriteStartObject();
        w.WriteString("id", Key(stream.Tenant, d));
        w.WriteString("tenant", stream.Tenant.ToString("N"));
        w.WriteString("type", d.Type);
        w.WriteString("doc", d.Id.ToString("N"));
        w.WriteString("aggregate", d.Aggregate.ToString("N"));
        w.WriteNumber("version", stream.Version);
        w.WriteString("title", d.Title);
        var german = string.Equals(d.Language, "de", StringComparison.OrdinalIgnoreCase);
        foreach (var (field, text) in new[] { ("a", d.A), ("b", d.B), ("c", d.C), ("d", d.D) })
        {
            if (text is null)
                continue;
            w.WriteString(field, text);                     // folded (Typesense's default normalisation) and stemmed English
            if (german)
                w.WriteString(field + "_de", text);         // German stemming; the "de" locale keeps umlauts, so the folded field stays too
        }
        w.WriteStartArray("exact");
        foreach (var e in d.Exact)
            w.WriteStringValue(e);
        w.WriteEndArray();
        w.WriteStartArray("audience");
        foreach (var a in d.Audience)
            w.WriteStringValue(a);
        w.WriteEndArray();
        w.WriteBoolean("open", d.Audience.Count == 0);
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
        var positives = clauses.Where(static c => !c.Excluded).ToList();
        // counts over several searches come from their merged hits: fetch up to the count bound then
        var wanted = request.Counts ? SearchLimits.CountCap : Math.Min(SearchLimits.MaxDepth, offset + request.Limit + 1);
        var merged = new Dictionary<string, (SearchHit Hit, double Score)>(StringComparer.Ordinal);
        Dictionary<string, long>? counts = request.Counts ? [] : null;
        var capped = false;
        var collection = CollectionOf(version);
        var queries = new List<string>();
        if (positives.Count > 0)
            foreach (var group in Groups(request))           // Typesense tokenizes a query by its first field's locale: one search per locale
                queries.AddRange(Combinations(clauses).Select(q => Search(request, q, typo: false, fields: group)));
        if (request.ExactToken is { } token)
            queries.Add(Search(request, "*", typo: false, extraFilter: $"exact:={Quoted(token)}"));
        if (queries.Count > 0)
        {
            var found = await Run(collection, queries, wanted, cancellationToken).ConfigureAwait(false);
            var exactIndex = request.ExactToken is null ? -1 : queries.Count - 1;
            for (var i = 0; i < found.Count; i++)
                foreach (var (hit, raw, _) in found[i].Hits)
                {
                    var score = i == exactIndex ? 1e18 : raw;
                    if (!merged.TryGetValue(hit.Type + hit.Id, out var seen) || seen.Score < score)
                        merged[hit.Type + hit.Id] = (hit with { Score = score }, score);
                }
            if (counts is not null)
            {
                if (found.Count == 1)
                    foreach (var (type, n) in found[0].Counts)
                        counts[type] = n;
                else
                {
                    foreach (var (hit, _) in merged.Values)
                        counts[hit.Type] = counts.GetValueOrDefault(hit.Type) + 1;
                    capped = found.Any(f => f.Found > f.Hits.Count);
                }
            }
            if (merged.Count == 0 && offset == 0 && positives.Count > 0)
            {
                var fuzzy = await Run(collection, [.. Combinations(clauses).Select(q => Search(request, q, typo: true))], PerPage, cancellationToken).ConfigureAwait(false);
                foreach (var (hit, raw, a) in fuzzy.SelectMany(static f => f.Hits))
                    if (positives.All(c => c.Alternatives.Any(alt => SearchText.TypoMatch(alt, SearchText.Words(a)))))
                        merged.TryAdd(hit.Type + hit.Id, (hit with { Snippet = null, Highlights = [], Score = raw }, raw));
                if (counts is not null)
                {
                    counts.Clear();
                    foreach (var (hit, _) in merged.Values)
                        counts[hit.Type] = counts.GetValueOrDefault(hit.Type) + 1;
                }
            }
        }
        var ordered = merged.Values.OrderByDescending(static m => m.Score).ThenBy(static m => m.Hit.Id).Select(static m => m.Hit).Take(SearchLimits.MaxDepth).ToList();
        var page = ordered.Skip(offset).Take(request.Limit).Select(h => request.Snippets ? h : h with { Snippet = null, Highlights = [] }).ToList();
        var next = offset + page.Count < ordered.Count ? SearchText.Cursor(request, version, offset + page.Count) : null;
        return new SearchPage(page, next, counts, capped, version, checkpoint.Position, checkpoint.State);
    }

    /// <summary>One Typesense q per combination of the OR alternatives (at most 8), with phrases quoted and exclusions as <c>-word</c>.</summary>
    private static IEnumerable<string> Combinations(IReadOnlyList<SearchClause> clauses)
    {
        IEnumerable<string> combos = [""];
        foreach (var clause in clauses)
        {
            if (clause.Excluded)
            {
                var excluded = string.Join(' ', clause.Alternatives.Select(static a => a.Count == 1 ? "-" + a[0] : "-\"" + string.Join(' ', a) + "\""));
                combos = combos.Select(c => (c + " " + excluded).Trim());
                continue;
            }
            var options = clause.Alternatives.Select(static a => a.Count == 1 ? a[0] : "\"" + string.Join(' ', a) + "\"").ToList();
            combos = combos.SelectMany(c => options.Select(o => (c + " " + o).Trim())).Take(MaxCombinations).ToList();
        }
        return combos;
    }

    /// <summary>The field groups to search: the folded base fields always; the German fields unless the query is English.</summary>
    private static IEnumerable<string[]> Groups(SearchRequest request)
    {
        yield return Weights;
        if (!string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase))
            yield return [.. Weights.Select(static w => w + "_de")];
    }

    private static string Search(SearchRequest request, string q, bool typo, string? extraFilter = null, string[]? fields = null)
    {
        fields = typo ? ["a"] : fields ?? Weights;
        var weights = string.Join(',', fields.Select(static f => f[0] switch { 'a' => "15", 'b' => "6", 'c' => "3", _ => "1" }));
        var filters = new List<string> { $"tenant:={Quoted(request.Tenant.ToString("N"))}" };
        if (request.Types is { } types)
            filters.Add($"type:=[{string.Join(',', types.Select(Quoted))}]");
        if (request.Audience is { } audience)
            filters.Add(audience.Count == 0 ? "open:=true" : $"(open:=true || audience:=[{string.Join(',', audience.Select(Quoted))}])");
        if (extraFilter is not null)
            filters.Add(extraFilter);
        var sb = new StringBuilder();
        using (var buffer = new MemoryStream())
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("collection", "{collection}");
            w.WriteString("q", q);
            w.WriteString("query_by", string.Join(',', fields));
            if (q != "*")
                w.WriteString("query_by_weights", weights);
            w.WriteString("filter_by", string.Join(" && ", filters));
            w.WriteString("sort_by", q == "*" ? "doc:asc" : "_text_match:desc,doc:asc");
            w.WriteString("num_typos", typo ? "1" : "0");
            w.WriteNumber("typo_tokens_threshold", typo ? 1 : 0);
            w.WriteNumber("drop_tokens_threshold", 0);
            w.WriteString("prefix", !typo && request.Prefix ? "true" : "false");
            w.WriteString("facet_by", "type");
            w.WriteNumber("max_facet_values", 200);
            w.WriteString("include_fields", "type,doc,aggregate,title,a,a_de");
            w.WriteString("highlight_fields", typo ? "none" : "b,b_de,a,a_de");
            w.WriteString("highlight_start_tag", SearchSnippets.Open);
            w.WriteString("highlight_end_tag", SearchSnippets.Close);
            w.WriteBoolean("exhaustive_search", false);
            w.WriteEndObject();
            w.Flush();
            sb.Append(Encoding.UTF8.GetString(buffer.ToArray()));
        }
        return sb.ToString();
    }

    private sealed record Result(List<(SearchHit Hit, double Score, string? A)> Hits, long Found, Dictionary<string, long> Counts);

    private async Task<List<Result>> Run(string collection, IReadOnlyList<string> searches, int wanted, CancellationToken cancellationToken)
    {
        var results = searches.Select(static _ => new Result([], 0, [])).ToList();
        var pages = Math.Max(1, (wanted + PerPage - 1) / PerPage);
        for (var page = 1; page <= pages; page++)
        {
            var body = "{\"searches\":[" + string.Join(',', searches.Select(s => s.Replace("\"{collection}\"", "\"" + collection + "\"", StringComparison.Ordinal)
                .TrimEnd('}') + $",\"per_page\":{PerPage},\"page\":{page}}}")) + "]}";
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var answer = await EngineJson.Send(_http, HttpMethod.Post, "/multi_search", content, cancellationToken).ConfigureAwait(false);
            var i = 0;
            var more = false;
            foreach (var result in answer.RootElement.GetProperty("results").EnumerateArray())
            {
                if (result.TryGetProperty("error", out var error))
                    throw new HttpRequestException("Typesense search failed: " + error.GetString());
                var found = results[i];
                results[i] = found with { Found = result.GetProperty("found").GetInt64() };
                foreach (var h in result.GetProperty("hits").EnumerateArray())
                {
                    var d = h.GetProperty("document");
                    string? snippet = null;
                    if (h.TryGetProperty("highlights", out var highlights) && highlights.GetArrayLength() > 0 && highlights[0].TryGetProperty("snippet", out var s))
                        snippet = s.GetString();
                    var a = d.TryGetProperty("a", out var av) ? av.GetString() : d.TryGetProperty("a_de", out var ad) ? ad.GetString() : null;
                    found.Hits.Add((SearchSnippets.Hit(d.GetProperty("type").GetString()!, Guid.ParseExact(d.GetProperty("doc").GetString()!, "N"),
                        Guid.ParseExact(d.GetProperty("aggregate").GetString()!, "N"), d.GetProperty("title").GetString()!, 0, snippet),
                        h.TryGetProperty("text_match", out var tm) ? tm.GetDouble() : 0, a));
                }
                if (page == 1 && result.TryGetProperty("facet_counts", out var facets) && facets.GetArrayLength() > 0)
                    foreach (var c in facets[0].GetProperty("counts").EnumerateArray())
                        found.Counts[c.GetProperty("value").GetString()!] = c.GetProperty("count").GetInt64();
                more |= results[i].Found > page * PerPage;
                i++;
            }
            if (!more)
                break;
        }
        return results;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SearchHit>> Documents(Guid tenant, IReadOnlyCollection<Guid> aggregates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        var request = new SearchRequest(tenant, "*");
        var search = Search(request, "*", typo: false, extraFilter: $"aggregate:=[{string.Join(',', aggregates.Select(static a => Quoted(a.ToString("N"))))}]");
        var found = await Run(CollectionOf(_version), [search], SearchLimits.MaxDepth, cancellationToken).ConfigureAwait(false);
        return [.. found[0].Hits.Select(static h => h.Hit with { Snippet = null, Highlights = [] })];
    }
}
