using System.Text.Json;

namespace Probe.Search;

/// <summary>Meilisearch (MIT): documents in one index "docs", filterable tenant and type, searchable title then body (order = weight).
/// Writes are asynchronous tasks; <see cref="Load"/> waits for the last one.</summary>
public sealed class Meili(string url) : IEngine
{
    private readonly HttpClient _c = Http.Client(url);
    private long _lastTask = -1;
    public string Name => "meilisearch";

    public async Task Reset()
    {
        using (var _ = await Http.Send(_c, HttpMethod.Delete, "/indexes/docs", allowNotFound: true)) { }
        await Wait(await Task(HttpMethod.Post, "/indexes", Http.Json(w => { w.WriteStartObject(); w.WriteString("uid", "docs"); w.WriteString("primaryKey", "id"); w.WriteEndObject(); })));
        await Wait(await Task(HttpMethod.Patch, "/indexes/docs/settings", Http.Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("searchableAttributes"); w.WriteStringValue("title"); w.WriteStringValue("body"); w.WriteEndArray();
            w.WriteStartArray("filterableAttributes"); w.WriteStringValue("tenant"); w.WriteStringValue("type"); w.WriteEndArray();
            w.WriteEndObject();
        })));
    }

    private async Task<long> Task(HttpMethod m, string path, HttpContent body)
    {
        using var d = await Http.Send(_c, m, path, body);
        return _lastTask = d.RootElement.GetProperty("taskUid").GetInt64();
    }

    public async Task Wait(long task)
    {
        while (true)
        {
            using var d = await Http.Send(_c, HttpMethod.Get, $"/tasks/{task}");
            var status = d.RootElement.GetProperty("status").GetString();
            if (status == "succeeded") return;
            if (status is "failed" or "canceled") throw new InvalidOperationException("meili task " + task + ": " + d.RootElement.GetRawText());
            await System.Threading.Tasks.Task.Delay(2);
        }
    }

    public async Task Load(IEnumerable<Doc> docs)
    {
        long last = -1;
        foreach (var chunk in Http.Chunks(docs, 20_000))
            last = await Task(HttpMethod.Post, "/indexes/docs/documents", Http.Lines(chunk, (w, d) => Http.Doc(w, d, idNoDashes: true), "application/x-ndjson"));
        if (last >= 0) await Wait(last);
    }

    public async Task Upsert(Doc doc) => await Task(HttpMethod.Post, "/indexes/docs/documents", Http.Lines([doc], (w, d) => Http.Doc(w, d, idNoDashes: true), "application/x-ndjson"));

    public async Task Delete(IReadOnlyCollection<Guid> ids) => await Task(HttpMethod.Post, "/indexes/docs/documents/delete-batch",
        Http.Json(w => { w.WriteStartArray(); foreach (var id in ids) w.WriteStringValue(id.ToString("N")); w.WriteEndArray(); }));

    public Task WaitLast() => _lastTask >= 0 ? Wait(_lastTask) : System.Threading.Tasks.Task.CompletedTask;

    public async Task<Hits> Search(Query q)
    {
        using var d = await Http.Send(_c, HttpMethod.Post, "/indexes/docs/search", Http.Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("q", q.Text);
            w.WriteNumber("limit", q.Limit);
            var filters = new List<string>();
            if (q.Tenant is { } t) filters.Add($"tenant = \"{t:N}\"");
            if (q.Types is { Length: > 0 } types) filters.Add("type IN [" + string.Join(", ", types.Select(x => "\"" + x + "\"")) + "]");
            if (filters.Count > 0) w.WriteString("filter", string.Join(" AND ", filters));
            if (q.Facets) { w.WriteStartArray("facets"); w.WriteStringValue("type"); w.WriteEndArray(); }
            if (q.Snippets)
            {
                w.WriteStartArray("attributesToCrop"); w.WriteStringValue("body"); w.WriteEndArray();
                w.WriteNumber("cropLength", 12);
                w.WriteStartArray("attributesToHighlight"); w.WriteStringValue("body"); w.WriteEndArray();
            }
            w.WriteString("matchingStrategy", "all");
            // Meilisearch: typo tolerance and prefix search on the last word are on by default; Words disables neither
            w.WriteEndObject();
        }));
        var root = d.RootElement;
        var items = new List<Hit>();
        string? snippet = null;
        foreach (var h in root.GetProperty("hits").EnumerateArray())
        {
            items.Add(new Hit(Guid.ParseExact(h.GetProperty("id").GetString()!, "N"), h.GetProperty("type").GetString()!, 0));
            if (q.Snippets && snippet is null && h.TryGetProperty("_formatted", out var f)) snippet = f.GetProperty("body").GetString();
        }
        Dictionary<string, long>? per = null;
        if (q.Facets && root.TryGetProperty("facetDistribution", out var fd) && fd.TryGetProperty("type", out var ft))
        {
            per = [];
            foreach (var p in ft.EnumerateObject()) per[p.Name] = p.Value.GetInt64();
        }
        return new Hits(items, root.GetProperty("estimatedTotalHits").GetInt64(), per, snippet);
    }

    public async Task<long> Size()
    {
        using var d = await Http.Send(_c, HttpMethod.Get, "/stats");
        return d.RootElement.GetProperty("databaseSize").GetInt64();
    }

    public ValueTask DisposeAsync() { _c.Dispose(); return ValueTask.CompletedTask; }
}
