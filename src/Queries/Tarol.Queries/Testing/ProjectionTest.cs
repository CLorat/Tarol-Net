using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Queries;

/// <summary>
/// Tests a projection and its queries without a database: an <see cref="InMemoryAggregateStore"/> (or any feed), an
/// <see cref="InMemoryReadModelStore{TRow}"/> and the real <see cref="ProjectionHost{TRow}"/>. Given aggregates are committed
/// to the store; <see cref="CatchUp"/> runs the host to the head of the feed; then read the rows or search them.
/// </summary>
/// <example>
/// <code>
/// var test = ProjectionTest&lt;AuctionRow&gt;.Create().Given(auction, tenant);
/// await test.CatchUp();
/// test.Row(auction.Id.Value, tenant).ShouldNotBeNull().Bids.ShouldBe(0);
/// var page = (await test.Search(new ListQuery(tenant.Value) { Sort = "ends_at" })).GetValueOrThrow();
/// </code>
/// </example>
/// <typeparam name="TRow">The row record.</typeparam>
public sealed class ProjectionTest<TRow>
    where TRow : class, IProjection<TRow>
{
    private readonly Actor _actor = Actor.User("test-user");
    private readonly TimeProvider _time;

    private ProjectionTest(IEventFeed feed, InMemoryAggregateStore? store, ProjectionOptions? options, TimeProvider time)
    {
        _time = time;
        Feed = feed;
        Store = store;
        ReadModels = new InMemoryReadModelStore<TRow>();
        Host = new ProjectionHost<TRow>(feed, ReadModels, options);
    }

    /// <summary>The aggregate store the given aggregates are committed to (<see langword="null"/> over a foreign feed).</summary>
    public InMemoryAggregateStore? Store { get; }

    /// <summary>The feed the host reads.</summary>
    public IEventFeed Feed { get; }

    /// <summary>The read store.</summary>
    public InMemoryReadModelStore<TRow> ReadModels { get; }

    /// <summary>The host (call <see cref="ProjectionHost.RunOnce"/> for a single batch, inspect its position).</summary>
    public ProjectionHost<TRow> Host { get; }

    /// <summary>A test over a new in-memory store.</summary>
    /// <param name="time">The store's clock (event times); default <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">The host's options (a small <see cref="ProjectionOptions.Batch"/> tests batching).</param>
    /// <returns>The test.</returns>
    public static ProjectionTest<TRow> Create(TimeProvider? time = null, ProjectionOptions? options = null)
    {
        time ??= TimeProvider.System;
        var store = new InMemoryAggregateStore(time);
        return new ProjectionTest<TRow>(store, store, options, time);
    }

    /// <summary>A test over an existing store (e.g. the one of a <c>ChangeTest</c>), or any feed.</summary>
    /// <param name="feed">The feed; an <see cref="InMemoryAggregateStore"/> also takes <see cref="Given{T}"/>.</param>
    /// <param name="options">The host's options.</param>
    /// <param name="time">The clock of correlation ids; default <see cref="TimeProvider.System"/>.</param>
    /// <returns>The test.</returns>
    public static ProjectionTest<TRow> Over(IEventFeed feed, ProjectionOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return new ProjectionTest<TRow>(feed, feed as InMemoryAggregateStore, options, time ?? TimeProvider.System);
    }

    /// <summary>Commits <paramref name="aggregate"/>'s uncommitted events in <paramref name="tenant"/> (none by default).</summary>
    /// <typeparam name="T">The aggregate type.</typeparam>
    /// <param name="aggregate">The aggregate, fresh from its factory or after a behaviour.</param>
    /// <param name="tenant">The tenant of the stream.</param>
    /// <returns>This test.</returns>
    /// <exception cref="InvalidOperationException">The test runs over a foreign feed, an invariant fails, or the store refuses it.</exception>
    public ProjectionTest<TRow> Given<T>(T aggregate, Option<Id<ITenant>> tenant = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var store = Store ?? throw new InvalidOperationException("Given needs the test's own store; this test runs over a foreign feed.");
        if (AggregateChanges.Prepare(aggregate).TryGetValue(out Violation? broken))
            throw new InvalidOperationException($"The given {typeof(T).Name} breaks an invariant: {broken}.");
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;
        var context = new OperationContext(_actor, tenant, Id<ICorrelation>.New(_time), Option.None);
        var appended = store.Append(aggregate, expected, context, Option.None, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (appended.TryGetValue(out Violation? refused))
            throw new InvalidOperationException($"The store refused the given {typeof(T).Name}: {refused}.");
        AggregateChanges.Committed(aggregate);
        return this;
    }

    /// <summary>Runs the host until it has read every committed event.</summary>
    /// <returns>The number of feed events read.</returns>
    public Task<long> CatchUp() => Host.CatchUp(CancellationToken.None);

    /// <summary>The rows of <paramref name="tenant"/> (none by default), ordered by id.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The rows.</returns>
    public IReadOnlyList<TRow> Rows(Option<Id<ITenant>> tenant = default)
    {
        var key = TenantOf(tenant);
        var rows = ReadModels.Rows.Where(r => r.Key.Tenant == key).ToList();
        rows.Sort(static (a, b) => ColumnValues.CompareIds(a.Key.Id, b.Key.Id));
        return [.. rows.Select(static r => r.Row)];
    }

    /// <summary>The row of one stream, or <see langword="null"/>.</summary>
    /// <param name="streamId">The stream id.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The row.</returns>
    public TRow? Row(Guid streamId, Option<Id<ITenant>> tenant = default)
        => ReadModels.Rows.FirstOrDefault(r => r.Key == new RowKey(TenantOf(tenant), streamId))?.Row;

    /// <summary>A list query on the read store.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The page, or the <see cref="Invalid"/> a bad cursor, limit or offset gives.</returns>
    public ValueTask<Result<Page<TRow>>> Search(ListQuery query) => ReadModels.Search(query, CancellationToken.None);

    private static Guid TenantOf(Option<Id<ITenant>> tenant) => tenant.TryGetValue(out Id<ITenant> id) ? id.Value : Guid.Empty;
}
