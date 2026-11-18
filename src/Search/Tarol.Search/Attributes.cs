namespace Tarol.Search;

/// <summary>How much a searchable member counts in the rank: <see cref="A"/> most, <see cref="D"/> least.</summary>
public enum SearchWeight
{
    /// <summary>The heaviest: titles, names.</summary>
    A,

    /// <summary>The default: descriptions.</summary>
    B,

    /// <summary>Lighter: notes, tags.</summary>
    C,

    /// <summary>The lightest.</summary>
    D,
}

/// <summary>
/// On an aggregate class: the aggregate is searchable (its documents go to the index); the optional label names its type for
/// display. On a member of the aggregate's state or of one of its entities: the member's text is indexed with
/// <see cref="Weight"/>. The generator (inside Tarol.Search) implements <see cref="ISearchable{T}"/> on the aggregate.
/// </summary>
/// <example>
/// <code>
/// [Searchable("Artwork")]
/// public partial class Artwork : AggregateRoot&lt;Artwork&gt;
/// {
///     [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
///     [Searchable] public string Description { get; private set; } = "";
///     [PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; }
///     public EntitySet&lt;Edition&gt; Editions { get; } = [];
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class SearchableAttribute : Attribute
{
    /// <summary>Marks an aggregate as searchable, or a member with weight <see cref="SearchWeight.B"/>.</summary>
    public SearchableAttribute()
    {
    }

    /// <summary>Marks an aggregate as searchable with a display label for its type.</summary>
    /// <param name="label">The display name of the type ("Artwork").</param>
    public SearchableAttribute(string label) => Label = label;

    /// <summary>Marks a member with a weight.</summary>
    /// <param name="weight">How much the member counts in the rank.</param>
    public SearchableAttribute(SearchWeight weight) => Weight = weight;

    /// <summary>The display name of the aggregate's type (on a class); <see langword="null"/> = the class name.</summary>
    public string? Label { get; }

    /// <summary>The member's weight (default <see cref="SearchWeight.B"/>).</summary>
    public SearchWeight Weight { get; } = SearchWeight.B;

    /// <summary>
    /// Index the member only as an exact token (the whole normalised value), never as text. Required on a
    /// <c>[PersonalData]</c> member (TSRCH003): the token is an HMAC under the tenant's lookup key.
    /// </summary>
    public bool Exact { get; init; }
}

/// <summary>The member whose value is the document's display title (one per aggregate or entity). Entities without one show their aggregate's title.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SearchTitleAttribute : Attribute;

/// <summary>
/// The member whose value (a string, a <see cref="Guid"/>, or a collection of them) says who may see the document: the search
/// adds "the audience is empty, or it overlaps the caller's subject and roles" (GS-7). Entities inherit their aggregate's audience.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SearchAudienceAttribute : Attribute;

/// <summary>The member whose value (an ISO 639-1 code such as "de" or "en") is the language of the aggregate's documents (GS-10).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SearchLanguageAttribute : Attribute;
