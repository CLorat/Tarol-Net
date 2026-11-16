namespace Tarol.Queries;

/// <summary>
/// Declares a row record as a projection (docs/architecture/queries.md §3, QR-2): a static <c>When(event)</c> creates the
/// row of a stream from its first relevant event, instance <c>When(event)</c> methods return the next row. The generator
/// implements <see cref="IProjection{TRow}"/> on the record: the switch over the events and the column map.
/// </summary>
/// <example>
/// <code>
/// [Projection("auction-list", Version = 1)]
/// public sealed partial record AuctionRow(Guid Id, string Title, [Sort] DateTimeOffset EndsAt, AuctionStatus Status)
/// {
///     public static AuctionRow When(Auction.Opened e, RecordedEvent r) => new(r.StreamId, e.Title, e.EndsAt, AuctionStatus.Open);
///     public AuctionRow When(Auction.Closed e) => this with { Status = AuctionStatus.Closed };
/// }
/// </code>
/// </example>
/// <param name="name">The projection's name: lowercase letters, digits and hyphens, at most 40 characters, starting with a letter.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProjectionAttribute(string name) : Attribute
{
    /// <summary>The projection's name; with <see cref="Version"/> it names the read table (<c>rm_&lt;name&gt;_v&lt;version&gt;</c>).</summary>
    public string Name { get; } = name;

    /// <summary>
    /// The version of the row's shape and logic (default 1). Raising it is the rebuild (QR-3): the new version is built from
    /// the start of the feed next to the old one, which keeps serving the code that reads it.
    /// </summary>
    public int Version { get; set; } = 1;
}

/// <summary>
/// Marks a row member as a sort of list queries (QR-4): the read table gets an index (tenant, column, id), and query
/// records may name it in their sort enum. Sort members are non-nullable scalars (TQRY004).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class SortAttribute : Attribute;

/// <summary>Marks a member of a query record's sort enum as descending (the row id, the tie-break, descends with it).</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class DescendingAttribute : Attribute;

/// <summary>
/// The marker of a list query record over the projection <typeparamref name="TRow"/>: <c>Tarol.Changes.AspNetCore</c>
/// generates a GET endpoint for it (<c>[Endpoint("GET /auctions")]</c>, step 12C).
/// </summary>
/// <typeparam name="TRow">The projection's row record.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A marker the endpoint generator reads; it names the projection a query record lists (queries.md §5).")]
public interface IQuery<TRow>
    where TRow : class, IProjection<TRow>;
