using System.Text;
using Tarol.Primitives;

namespace Tarol.Search;

/// <summary>
/// What the generator implements (explicitly) on a <c>[Searchable]</c> aggregate: the shape of its documents and the map from a
/// loaded aggregate to them. No reflection; generic code calls <c>T.Documents(…)</c>.
/// </summary>
/// <typeparam name="T">The aggregate.</typeparam>
public interface ISearchable<T>
    where T : AggregateRoot<T>, IAggregateRoot<T>, ISearchable<T>
{
    /// <summary>The document types of the aggregate and its entities, and the fingerprint of their fields and weights.</summary>
    static abstract SearchShape Shape { get; }

    /// <summary>Adds the aggregate's documents: one for the aggregate, one per entity with a searchable member.</summary>
    /// <param name="aggregate">The loaded aggregate (its current state; erased personal fields read as <see langword="null"/>).</param>
    /// <param name="into">Receives the documents.</param>
    static abstract void Documents(T aggregate, ICollection<SearchDocument> into);
}

/// <summary>A document type: the aggregate's stream type, or <c>&lt;stream type&gt;.&lt;entity&gt;</c> for an entity.</summary>
/// <param name="Type">The type as stored and filtered ("artwork", "artwork.edition").</param>
/// <param name="Label">The display name ("Artwork", "Edition").</param>
/// <param name="Aggregate">The stream type of the aggregate it is authorized through.</param>
public sealed record SearchType(string Type, string Label, string Aggregate);

/// <summary>The document types of one searchable aggregate and the fingerprint of their searchable members (GS-5).</summary>
public sealed class SearchShape
{
    /// <summary>Creates a shape.</summary>
    /// <param name="streamType">The aggregate's stream type.</param>
    /// <param name="types">Its document types (the aggregate's first).</param>
    /// <param name="fingerprint">A hash of every searchable member, weight and flag; a change needs a new index version.</param>
    public SearchShape(string streamType, IReadOnlyList<SearchType> types, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamType);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        StreamType = streamType;
        Types = types;
        Fingerprint = fingerprint;
    }

    /// <summary>The aggregate's stream type.</summary>
    public string StreamType { get; }

    /// <summary>Its document types.</summary>
    public IReadOnlyList<SearchType> Types { get; }

    /// <summary>The fingerprint of the searchable members.</summary>
    public string Fingerprint { get; }

    /// <summary>The shape of <typeparamref name="T"/> (the generated code calls it).</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="label">The aggregate's display label.</param>
    /// <param name="fingerprint">The generated fingerprint.</param>
    /// <param name="entities">The entities' type suffixes and labels.</param>
    /// <returns>The shape.</returns>
    public static SearchShape Of<T>(string label, string fingerprint, params (string Suffix, string Label)[] entities)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(entities);
        var types = new List<SearchType>(entities.Length + 1) { new(T.StreamType, label, T.StreamType) };
        foreach (var (suffix, entityLabel) in entities)
            types.Add(new SearchType(T.StreamType + "." + suffix, entityLabel, T.StreamType));
        return new SearchShape(T.StreamType, types, fingerprint);
    }
}

/// <summary>
/// One document of the index: a hit's own type and id, the aggregate it is shown and authorized through, its title, its text per
/// weight, its exact values (blinded before they are stored), its audience and language.
/// </summary>
/// <param name="Type">The document type ("artwork", "artwork.edition").</param>
/// <param name="Id">The aggregate's or the entity's id.</param>
/// <param name="Aggregate">The aggregate's id (equal to <paramref name="Id"/> for the aggregate's own document).</param>
/// <param name="Title">The display title.</param>
public sealed record SearchDocument(string Type, Guid Id, Guid Aggregate, string Title)
{
    /// <summary>The text of weight A.</summary>
    public string? A { get; init; }

    /// <summary>The text of weight B.</summary>
    public string? B { get; init; }

    /// <summary>The text of weight C.</summary>
    public string? C { get; init; }

    /// <summary>The text of weight D.</summary>
    public string? D { get; init; }

    /// <summary>Exact values: the generator fills them with plaintext; the host replaces each with its blind token before the write.</summary>
    public IReadOnlyList<string> Exact { get; init; } = [];

    /// <summary>Who may see the document (empty: everyone allowed to read its aggregate type).</summary>
    public IReadOnlyList<string> Audience { get; init; } = [];

    /// <summary>The document's language (ISO 639-1), or <see langword="null"/> for the tenant's default.</summary>
    public string? Language { get; init; }
}

/// <summary>Helpers the generated code calls.</summary>
public static class SearchDocuments
{
    /// <summary>The stream type of <typeparamref name="T"/> (the generated code calls it).</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <returns>Its stored stream type.</returns>
    public static string StreamTypeOf<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>
        => T.StreamType;

    /// <summary>Joins the non-empty texts with a space; <see langword="null"/> when there is none.</summary>
    /// <param name="values">The member values.</param>
    /// <returns>The text.</returns>
    public static string? Join(params ReadOnlySpan<string?> values)
    {
        StringBuilder? text = null;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            text ??= new StringBuilder();
            if (text.Length > 0)
                text.Append(' ');
            text.Append(value);
        }
        return text?.ToString();
    }

    /// <summary>The text of a collection member (tags, names).</summary>
    /// <param name="values">The values.</param>
    /// <returns>The values joined with a space, or <see langword="null"/>.</returns>
    public static string? Text(IEnumerable<string?>? values) => values is null ? null : Join([.. values]);

    /// <summary>The exact values that are not empty (a <see langword="null"/> after an erasure drops out).</summary>
    /// <param name="values">The member values.</param>
    /// <returns>The values.</returns>
    public static IReadOnlyList<string> Exact(params ReadOnlySpan<string?> values)
    {
        List<string>? list = null;
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                (list ??= []).Add(value);
        return list is null ? [] : list;
    }

    /// <summary>The audience values of a member: a string, a <see cref="Guid"/>, or a collection of them.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The non-empty values.</returns>
    public static IReadOnlyList<string> Audience(IEnumerable<string?>? values)
        => values is null ? [] : [.. values.Where(static v => !string.IsNullOrWhiteSpace(v)).Select(static v => v!)];
}
