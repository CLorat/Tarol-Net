using Microsoft.Extensions.Primitives;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// <c>If-None-Match</c> as RFC 9110 §13.1.2 defines it, shared by views, lists and search (BC-7): <c>*</c> matches any
/// representation; otherwise a comma-separated list of entity tags, each compared weakly (<c>W/</c> ignored on both sides).
/// Commas inside an opaque tag are part of it.
/// </summary>
internal static class EntityTags
{
    /// <summary>Whether an <c>If-None-Match</c> header matches <paramref name="etag"/> by the weak comparison.</summary>
    /// <param name="ifNoneMatch">The header's values.</param>
    /// <param name="etag">The current entity tag, strong (<c>"5"</c>) or weak (<c>W/"…"</c>).</param>
    /// <returns>Whether the condition is false, i.e. the answer is 304.</returns>
    public static bool Matches(StringValues ifNoneMatch, string etag)
    {
        var current = Opaque(etag);
        foreach (var header in ifNoneMatch)
        {
            if (header is null)
                continue;
            var rest = header.AsSpan();
            while (true)
            {
                rest = rest.TrimStart(" \t,");
                if (rest.IsEmpty)
                    break;
                if (rest[0] == '*')
                    return true;
                if (rest.StartsWith("W/", StringComparison.Ordinal))
                    rest = rest[2..];
                if (rest.IsEmpty || rest[0] != '"')
                    break;                                                   // malformed: no further tag can be trusted
                var close = rest[1..].IndexOf('"');
                if (close < 0)
                    break;
                if (rest[..(close + 2)].SequenceEqual(current))
                    return true;
                rest = rest[(close + 2)..];
            }
        }
        return false;
    }

    private static ReadOnlySpan<char> Opaque(string etag) => etag.StartsWith("W/", StringComparison.Ordinal) ? etag.AsSpan(2) : etag;
}
