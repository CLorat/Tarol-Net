using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Probe.Search;

/// <summary>Hand-written JSON over HttpClient: Utf8JsonWriter out, JsonDocument in. No engine SDK, no reflection.</summary>
public static class Http
{
    public static HttpClient Client(string baseUrl, params (string Name, string Value)[] headers)
    {
        var c = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 16, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(30),
        };
        foreach (var (n, v) in headers) c.DefaultRequestHeaders.TryAddWithoutValidation(n, v);
        return c;
    }

    public static ByteArrayContent Json(Action<Utf8JsonWriter> write, string mediaType = "application/json")
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) write(w);
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    /// <summary>NDJSON: one object per line (Meilisearch, Typesense import, OpenSearch bulk).</summary>
    public static ByteArrayContent Lines<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> write, string mediaType, Action<Utf8JsonWriter, T>? header = null)
    {
        var buffer = new ArrayBufferWriter<byte>(1 << 20);
        var options = new JsonWriterOptions { SkipValidation = true };
        foreach (var item in items)
        {
            if (header is not null)
            {
                using (var h = new Utf8JsonWriter(buffer, options)) header(h, item);
                buffer.Write("\n"u8);
            }
            using (var w = new Utf8JsonWriter(buffer, options)) write(w, item);
            buffer.Write("\n"u8);
        }
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    public static async Task<JsonDocument> Send(HttpClient c, HttpMethod method, string path, HttpContent? body = null, bool allowNotFound = false)
    {
        using var req = new HttpRequestMessage(method, path) { Content = body };
        using var res = await c.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (!res.IsSuccessStatusCode && !(allowNotFound && res.StatusCode == System.Net.HttpStatusCode.NotFound))
            throw new InvalidOperationException($"{method} {path}: {(int)res.StatusCode} {Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 400)))}");
        return bytes.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes);
    }

    public static void Doc(Utf8JsonWriter w, Doc d, string idName = "id", bool idNoDashes = false)
    {
        w.WriteStartObject();
        w.WriteString(idName, idNoDashes ? d.Id.ToString("N") : d.Id.ToString());
        w.WriteString("tenant", d.Tenant.ToString("N"));
        w.WriteString("type", d.Type);
        w.WriteString("title", d.Title);
        w.WriteString("body", d.Body);
        w.WriteEndObject();
    }

    public static IEnumerable<T[]> Chunks<T>(IEnumerable<T> items, int size)
    {
        var chunk = new List<T>(size);
        foreach (var item in items)
        {
            chunk.Add(item);
            if (chunk.Count == size) { yield return [.. chunk]; chunk.Clear(); }
        }
        if (chunk.Count > 0) yield return [.. chunk];
    }
}
