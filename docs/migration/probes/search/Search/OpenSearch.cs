using System.Text.Json;

namespace Probe.Search;

/// <summary>OpenSearch (Apache-2.0): index "docs", one shard, no replica, keyword tenant and type, title (boost 2) and body with the
/// english analyzer. Near-real-time: a write is searchable after the next refresh (1 s by default).</summary>
public sealed class OpenSearch(string url) : IEngine
{
    private readonly HttpClient _c = Http.Client(url);
    public string Name => "opensearch";

    public async Task Reset()
    {
        using (var _ = await Http.Send(_c, HttpMethod.Delete, "/docs", allowNotFound: true)) { }
        using var d = await Http.Send(_c, HttpMethod.Put, "/docs", Http.Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("settings"); w.WriteNumber("number_of_shards", 1); w.WriteNumber("number_of_replicas", 0); w.WriteEndObject();
            w.WriteStartObject("mappings"); w.WriteStartObject("properties");
            foreach (var k in new[] { "tenant", "type" }) { w.WriteStartObject(k); w.WriteString("type", "keyword"); w.WriteEndObject(); }
            foreach (var k in new[] { "title", "body" }) { w.WriteStartObject(k); w.WriteString("type", "text"); w.WriteString("analyzer", "english"); w.WriteEndObject(); }
            w.WriteEndObject(); w.WriteEndObject();
            w.WriteEndObject();
        }));
    }

    private async Task Settings(string refresh)
    {
        using var _ = await Http.Send(_c, HttpMethod.Put, "/docs/_settings", Http.Json(w =>
        {
            w.WriteStartObject(); w.WriteStartObject("index"); w.WriteString("refresh_interval", refresh); w.WriteEndObject(); w.WriteEndObject();
        }));
    }

    public async Task Load(IEnumerable<Doc> docs)
    {
        await Settings("-1");
        foreach (var chunk in Http.Chunks(docs, 5_000))
            await Bulk(chunk, refresh: null);
        await Settings("1s");
        using (var _ = await Http.Send(_c, HttpMethod.Post, "/docs/_refresh")) { }
    }

    private async Task Bulk(IReadOnlyCollection<Doc> docs, string? refresh)
    {
        using var d = await Http.Send(_c, HttpMethod.Post, "/_bulk" + (refresh is null ? "" : "?refresh=" + refresh), Http.Lines(docs, (w, x) =>
        {
            w.WriteStartObject(); w.WriteString("tenant", x.Tenant.ToString("N")); w.WriteString("type", x.Type);
            w.WriteString("title", x.Title); w.WriteString("body", x.Body); w.WriteEndObject();
        }, "application/x-ndjson", (w, x) =>
        {
            w.WriteStartObject(); w.WriteStartObject("index"); w.WriteString("_index", "docs"); w.WriteString("_id", x.Id.ToString()); w.WriteEndObject(); w.WriteEndObject();
        }));
        if (d.RootElement.GetProperty("errors").GetBoolean())
            throw new InvalidOperationException("opensearch bulk: " + d.RootElement.GetRawText()[..400]);
    }

    public Task Upsert(Doc doc) => Bulk([doc], refresh: null);

    /// <summary>A feeder that wants read-your-writes waits for the refresh (refresh=wait_for).</summary>
    public Task UpsertWaitFor(Doc doc) => Bulk([doc], refresh: "wait_for");

    public async Task Delete(IReadOnlyCollection<Guid> ids)
    {
        using var _ = await Http.Send(_c, HttpMethod.Post, "/_bulk", Http.Lines(ids, (w, id) =>
        {
            w.WriteStartObject(); w.WriteStartObject("delete"); w.WriteString("_index", "docs"); w.WriteString("_id", id.ToString()); w.WriteEndObject(); w.WriteEndObject();
        }, "application/x-ndjson"));
    }

    public async Task<Hits> Search(Query q)
    {
        using var d = await Http.Send(_c, HttpMethod.Post, "/docs/_search", Http.Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("size", q.Limit);
            w.WriteBoolean("track_total_hits", true);
            w.WriteStartArray("_source"); w.WriteStringValue("type"); w.WriteEndArray();
            w.WriteStartObject("query"); w.WriteStartObject("bool");
            w.WriteStartObject("must"); w.WriteStartObject("multi_match");
            w.WriteString("query", q.Text);
            w.WriteStartArray("fields"); w.WriteStringValue("title^2"); w.WriteStringValue("body"); w.WriteEndArray();
            w.WriteString("operator", "and");
            if (q.Match == Match.Words) w.WriteString("type", "cross_fields");        // one term statistic across title and body
            if (q.Match == Match.Prefix) w.WriteString("type", "bool_prefix");
            if (q.Match == Match.Typo) w.WriteString("fuzziness", "AUTO");
            w.WriteEndObject(); w.WriteEndObject();
            w.WriteStartArray("filter");
            if (q.Tenant is { } t) { w.WriteStartObject(); w.WriteStartObject("term"); w.WriteString("tenant", t.ToString("N")); w.WriteEndObject(); w.WriteEndObject(); }
            if (q.Types is { Length: > 0 } types)
            {
                w.WriteStartObject(); w.WriteStartObject("terms"); w.WriteStartArray("type"); foreach (var x in types) w.WriteStringValue(x); w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject(); w.WriteEndObject();
            if (q.Facets)
            {
                w.WriteStartObject("aggs"); w.WriteStartObject("types"); w.WriteStartObject("terms"); w.WriteString("field", "type"); w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject();
            }
            if (q.Snippets)
            {
                w.WriteStartObject("highlight"); w.WriteStartObject("fields"); w.WriteStartObject("body"); w.WriteNumber("fragment_size", 80); w.WriteNumber("number_of_fragments", 1);
                w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject();
            }
            w.WriteEndObject();
        }));
        var root = d.RootElement;
        var hits = root.GetProperty("hits");
        var items = new List<Hit>();
        string? snippet = null;
        foreach (var h in hits.GetProperty("hits").EnumerateArray())
        {
            items.Add(new Hit(Guid.Parse(h.GetProperty("_id").GetString()!), h.GetProperty("_source").GetProperty("type").GetString()!,
                h.GetProperty("_score").ValueKind == JsonValueKind.Number ? h.GetProperty("_score").GetDouble() : 0));
            if (q.Snippets && snippet is null && h.TryGetProperty("highlight", out var hl)) snippet = hl.GetProperty("body")[0].GetString();
        }
        Dictionary<string, long>? per = null;
        if (q.Facets)
        {
            per = [];
            foreach (var b in root.GetProperty("aggregations").GetProperty("types").GetProperty("buckets").EnumerateArray())
                per[b.GetProperty("key").GetString()!] = b.GetProperty("doc_count").GetInt64();
        }
        return new Hits(items, hits.GetProperty("total").GetProperty("value").GetInt64(), per, snippet);
    }

    public async Task<long> Size()
    {
        using var d = await Http.Send(_c, HttpMethod.Get, "/docs/_stats/store");
        return d.RootElement.GetProperty("_all").GetProperty("primaries").GetProperty("store").GetProperty("size_in_bytes").GetInt64();
    }

    public ValueTask DisposeAsync() { _c.Dispose(); return ValueTask.CompletedTask; }
}
