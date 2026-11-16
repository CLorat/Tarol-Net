using Tarol.Primitives;

namespace Tarol.Queries;

/// <summary>
/// What the generator implements on a <see cref="ProjectionAttribute"/> row record (explicitly, so row members named
/// <c>Name</c> or <c>Version</c> stay free): the projection's identity, the stream type it reads, its columns and the
/// switch over its events. No reflection; generic code calls <c>TRow.Apply(…)</c>.
/// </summary>
/// <typeparam name="TRow">The row record.</typeparam>
public interface IProjection<TRow>
    where TRow : class, IProjection<TRow>
{
    /// <summary>The projection's name (<see cref="ProjectionAttribute.Name"/>).</summary>
    static abstract string Name { get; }

    /// <summary>The projection's version (<see cref="ProjectionAttribute.Version"/>).</summary>
    static abstract int Version { get; }

    /// <summary>The stream type of the aggregate whose events the projection reads; the host skips every other type.</summary>
    static abstract string StreamType { get; }

    /// <summary>The typed columns: every scalar member of the row except <c>Tenant</c>, <c>Id</c>, <c>Version</c> and <c>Doc</c>.</summary>
    static abstract IReadOnlyList<ProjectionColumn<TRow>> Columns { get; }

    /// <summary>
    /// Applies one event: the new row (a static <c>When</c> when <paramref name="row"/> is <see langword="null"/>, an instance
    /// <c>When</c> otherwise), the same instance when no <c>When</c> handles the event, or <see langword="null"/> when there is
    /// no row yet and the event does not create one.
    /// </summary>
    /// <param name="row">The stream's current row, or <see langword="null"/>.</param>
    /// <param name="recorded">The event with its commit facts.</param>
    /// <returns>The next row.</returns>
    static abstract TRow? Apply(TRow? row, RecordedEvent recorded);
}

/// <summary>Helpers the generated code calls.</summary>
public static class Projections
{
    /// <summary>The stream type of <typeparamref name="TAggregate"/> (the generated <c>StreamType</c> of a projection).</summary>
    /// <typeparam name="TAggregate">The aggregate whose events the projection reads.</typeparam>
    /// <returns>Its stored stream type.</returns>
    public static string StreamTypeOf<TAggregate>()
        where TAggregate : AggregateRoot<TAggregate>, IAggregateRoot<TAggregate>
        => TAggregate.StreamType;

    /// <summary>The read table of a projection version: <c>rm_&lt;name with underscores&gt;_v&lt;version&gt;</c>.</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <returns>The table name (without schema).</returns>
    public static string TableOf<TRow>()
        where TRow : class, IProjection<TRow>
        => $"rm_{TRow.Name.Replace('-', '_')}_v{TRow.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
