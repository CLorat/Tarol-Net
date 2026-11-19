using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tarol.Search.OpenSearch;

/// <summary>Hand-written JSON over HttpClient: Utf8JsonWriter out, JsonDocument in (no SDK, no reflection).</summary>
internal static class EngineJson
{
    public static ByteArrayContent Body(Action<Utf8JsonWriter> write, string mediaType = "application/json")
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    public static ByteArrayContent Lines(Action<Func<Utf8JsonWriter>> write)
    {
        var buffer = new ArrayBufferWriter<byte>(1 << 16);
        var first = true;
        write(() =>
        {
            if (!first)
                buffer.Write("\n"u8);
            first = false;
            return new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });
        });
        buffer.Write("\n"u8);
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        return content;
    }

    /// <summary>The last time the engine could not be reached, or <see langword="null"/> after the next answer (readiness reads it).</summary>
    public static DateTimeOffset? Unreachable(HttpClient http) => Failures.TryGetValue(http, out var at) ? at : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<HttpClient, DateTimeOffset> Failures = new();

    public static async Task<JsonDocument> Send(HttpClient http, HttpMethod method, string path, HttpContent? body, CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        HttpResponseMessage answered;
        try
        {
            answered = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Failures.TryRemove(http, out _);
        }
        catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
        {
            Failures[http] = TimeProvider.System.GetUtcNow();
            throw;
        }
        using var response = answered;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && !(allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound))
            throw new HttpRequestException($"{method} {path}: {(int)response.StatusCode} {Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 500)))}",
                null, response.StatusCode);
        return bytes.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes);
    }
}
