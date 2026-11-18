using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tarol.Results;

namespace Tarol.Search;

/// <summary>One clause of a parsed query: a word or phrase that must match (any of its alternatives), or one that must not.</summary>
/// <param name="Alternatives">The alternatives (joined by <c>OR</c>); each is one or more words that must be adjacent.</param>
/// <param name="Excluded">The clause must not match (<c>-word</c>).</param>
public sealed record SearchClause(IReadOnlyList<IReadOnlyList<string>> Alternatives, bool Excluded);

/// <summary>Text helpers every index shares: normalisation, the web-search parser, the cursor, the validation of a request.</summary>
public static class SearchText
{
    /// <summary>Lowercase, accents removed (Café → cafe), one space between words: the form of exact values and tokens.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalised text.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c == 'ß' ? "ss" : c.ToString());
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>The words of a text: normalised, split at everything that is not a letter or a digit.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The words in order.</returns>
    public static IReadOnlyList<string> Words(string? text)
    {
        var normalized = Normalize(text);
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var letter = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (letter && start < 0)
                start = i;
            else if (!letter && start >= 0)
            {
                words.Add(normalized[start..i]);
                start = -1;
            }
        }
        return words;
    }

    /// <summary>
    /// Parses web-search syntax like PostgreSQL's <c>websearch_to_tsquery</c>: words must all match, <c>"two words"</c> must be
    /// adjacent, <c>-word</c> must not match, <c>a OR b</c> needs either. Never fails: what is not syntax is a word.
    /// </summary>
    /// <param name="text">The query.</param>
    /// <returns>The clauses.</returns>
    public static IReadOnlyList<SearchClause> Parse(string? text)
    {
        var raw = new List<(IReadOnlyList<string> Words, bool Excluded, bool Or)>();
        var s = text ?? "";
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
                continue;
            }
            var excluded = false;
            if (s[i] == '-' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]))
            {
                excluded = true;
                i++;
            }
            string part;
            if (s[i] == '"')
            {
                var end = s.IndexOf('"', i + 1);
                part = end < 0 ? s[(i + 1)..] : s[(i + 1)..end];
                i = end < 0 ? s.Length : end + 1;
            }
            else
            {
                var end = i;
                while (end < s.Length && !char.IsWhiteSpace(s[end]))
                    end++;
                part = s[i..end];
                i = end;
                if (!excluded && part == "OR")
                {
                    if (raw.Count > 0)
                        raw[^1] = raw[^1] with { Or = true };
                    continue;
                }
            }
            var words = Words(part);
            if (words.Count > 0)
                raw.Add((words, excluded, false));
        }

        var clauses = new List<SearchClause>();
        List<IReadOnlyList<string>>? alternatives = null;
        foreach (var (words, excluded, or) in raw)
        {
            if (excluded)
            {
                clauses.Add(new SearchClause([words], true));
                continue;
            }
            (alternatives ??= []).Add(words);
            if (!or)
            {
                clauses.Add(new SearchClause(alternatives, false));
                alternatives = null;
            }
        }
        if (alternatives is not null)
            clauses.Add(new SearchClause(alternatives, false));
        return clauses;
    }

    /// <summary>Checks the parts of a request every index checks the same way.</summary>
    /// <param name="request">The request.</param>
    /// <returns>A violation for a field the caller sent, or none.</returns>
    public static Invalid? Validate(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > SearchLimits.MaxLimit)
            return Invalid.Field("limit", "Search.Limit", $"The limit must be between 1 and {SearchLimits.MaxLimit}.");
        if (request.Text is null || request.Text.Length > SearchLimits.MaxText)
            return Invalid.Field("q", "Search.Text", $"The query must have at most {SearchLimits.MaxText} characters.");
        return null;
    }

    /// <summary>The offset a cursor stands for, or a violation when it belongs to another query or version.</summary>
    /// <param name="request">The request whose <see cref="SearchRequest.After"/> is read.</param>
    /// <param name="version">The index version that answers.</param>
    /// <param name="offset">The offset (0 without a cursor).</param>
    /// <returns>A violation, or none.</returns>
    public static Invalid? Offset(SearchRequest request, int version, out int offset)
    {
        ArgumentNullException.ThrowIfNull(request);
        offset = 0;
        if (request.After is null)
            return null;
        Span<byte> bytes = stackalloc byte[64];
        if (!Base64Url.TryDecodeFromChars(request.After, bytes, out var length) || length != 16 + 8)
            return BadCursor;
        var at = BitConverter.ToInt32(bytes[..4]);
        var forVersion = BitConverter.ToInt32(bytes.Slice(4, 4));
        Span<byte> hash = stackalloc byte[16];
        QueryHash(request, version, hash);
        if (forVersion != version || at is < 1 or > SearchLimits.MaxDepth || !bytes.Slice(8, 16).SequenceEqual(hash))
            return BadCursor;
        offset = at;
        return null;
    }

    /// <summary>The cursor of the page after <paramref name="offset"/> hits, or <see langword="null"/> at <see cref="SearchLimits.MaxDepth"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="version">The index version that answered.</param>
    /// <param name="offset">The number of hits before the next page.</param>
    /// <returns>The opaque cursor.</returns>
    public static string? Cursor(SearchRequest request, int version, int offset)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (offset >= SearchLimits.MaxDepth)
            return null;
        Span<byte> bytes = stackalloc byte[24];
        BitConverter.TryWriteBytes(bytes[..4], offset);
        BitConverter.TryWriteBytes(bytes.Slice(4, 4), version);
        QueryHash(request, version, bytes.Slice(8, 16));
        return Base64Url.EncodeToString(bytes);
    }

    private static readonly Invalid BadCursor = Invalid.Field("after", "Search.Cursor", "The cursor is not valid for this query; start again without 'after'.");

    private static void QueryHash(SearchRequest request, int version, Span<byte> into)
    {
        var sb = new StringBuilder();
        sb.Append(version).Append('|').Append(request.Tenant).Append('|').Append(request.Text).Append('|').Append(request.Prefix).Append('|')
          .Append(request.Language).Append('|').Append(request.ExactToken).Append('|');
        foreach (var t in (request.Types ?? []).Order(StringComparer.Ordinal))
            sb.Append(t).Append(',');
        sb.Append('|');
        foreach (var a in (request.Audience ?? []).Order(StringComparer.Ordinal))
            sb.Append(a).Append(',');
        Span<byte> full = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), full);
        full[..16].CopyTo(into);
    }
}
