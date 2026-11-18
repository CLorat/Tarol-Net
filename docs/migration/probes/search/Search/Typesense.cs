using System.Text.Json;

namespace Probe.Search;

/// <summary>Typesense (GPL-3.0 server, run as a separate process): collection "docs", tenant and type as facet strings, query_by title
/// (weight 2) and body. Imports are synchronous: a document is searchable when the import returns.</summary>
public sealed class Typesense(string url, string key) : IEngine
{
    private readonly HttpClient _c = Http.Client(url, ("X-TYPESENSE-API-KEY", key));
    public string Name => "typesense";

    /// <summary>Typesense's per-field stemming (Snowball, english by default); on unless PROBE_TYPESENSE_STEM=0.</summary>
    public static bool Stem => Environment.GetEnvironmentVariable("PROBE_TYPESENSE_STEM") != "0";

    public async Task Reset()
    {
        using (var _ = await Http.Send(_c, HttpMethod.Delete, "/collections/docs", allowNotFound: true)) { }
        using var d = await Http.Send(_c, HttpMethod.Post, "/collections", Http.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("name", "docs");
            w.WriteStartArray("fields");
            foreach (var (n, facet) in new[] { ("tenant", true), ("type", true), ("title", false), ("body", false) })
            {
                w.WriteStartObject(); w.WriteString("name", n); w.WriteString("type", "string"); w.WriteBoolean("facet", facet);
                if (!facet) w.WriteBoolean("stem", Stem);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    public async Task Load(IEnumerable<Doc> docs)
    {
        foreach (var chunk in Http.Chunks(docs, 10_000))
            await Import(chunk);
    }

    private async Task Import(IReadOnlyCollection<Doc> docs)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/collections/docs/documents/import?action=upsert&batch_size=1000")
        {
            Content = Http.Lines(docs, (w, d) => Http.Doc(w, d), "text/plain"),
        };
        using var res = await _c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode || text.Contains("\"success\":false", StringComparison.Ordinal))
            throw new InvalidOperationException("typesense import: " + text[..Math.Min(400, text.Length)]);
    }

    public Task Upsert(Doc doc) => Import([doc]);

    public async Task Delete(IReadOnlyCollection<Guid> ids)
    {
        var filter = Uri.EscapeDataString("id:[" + string.Join(",", ids.Select(i => i.ToString())) + "]");
        using var _ = await Http.Send(_c, HttpMethod.Delete, $"/collections/docs/documents?filter_by={filter}");
    }

    public async Task<Hits> Search(Query q)
    {
        var filters = new List<string>();
        if (q.Tenant is { } t) filters.Add($"tenant:={t:N}");
        if (q.Types is { Length: > 0 } types) filters.Add("type:=[" + string.Join(",", types) + "]");
        var typos = q.Match == Match.Typo ? 2 : 0;          // Words/Prefix: exact words (as the other engines are asked)
        var prefix = q.Match == Match.Prefix ? "true" : "false";
        var path = $"/collections/docs/documents/search?q={Uri.EscapeDataString(q.Text)}&query_by=title,body&query_by_weights=2,1&per_page={q.Limit}"
            + $"&num_typos={typos}&prefix={prefix}&drop_tokens_threshold=0&typo_tokens_threshold={(q.Match == Match.Typo ? 1 : 0)}&exhaustive_search=false"
            + (filters.Count > 0 ? "&filter_by=" + Uri.EscapeDataString(string.Join(" && ", filters)) : "")
            + (q.Facets ? "&facet_by=type" : "")
            + (q.Snippets ? "&highlight_fields=body&snippet_threshold=12" : "&highlight_fields=none&enable_highlight_v1=false");
        using var d = await Http.Send(_c, HttpMethod.Get, path);
        var root = d.RootElement;
        var items = new List<Hit>();
        string? snippet = null;
        foreach (var h in root.GetProperty("hits").EnumerateArray())
        {
            var doc = h.GetProperty("document");
            items.Add(new Hit(Guid.Parse(doc.GetProperty("id").GetString()!), doc.GetProperty("type").GetString()!, h.TryGetProperty("text_match", out var s) ? s.GetDouble() : 0));
            if (q.Snippets && snippet is null && h.TryGetProperty("highlights", out var hl) && hl.GetArrayLength() > 0) snippet = hl[0].GetProperty("snippet").GetString();
        }
        Dictionary<string, long>? per = null;
        if (q.Facets && root.TryGetProperty("facet_counts", out var fc) && fc.GetArrayLength() > 0)
        {
            per = [];
            foreach (var c in fc[0].GetProperty("counts").EnumerateArray()) per[c.GetProperty("value").GetString()!] = c.GetProperty("count").GetInt64();
        }
        return new Hits(items, root.GetProperty("found").GetInt64(), per, snippet);
    }

    public async Task<long> Size()
    {
        using var d = await Http.Send(_c, HttpMethod.Get, "/metrics.json");
        return long.Parse(d.RootElement.GetProperty("typesense_memory_active_bytes").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() { _c.Dispose(); return ValueTask.CompletedTask; }
}
