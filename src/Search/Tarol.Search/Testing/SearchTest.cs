using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Search;

/// <summary>
/// Search without a database: an in-memory store and its feed, an index (in memory by default), the <see cref="SearchHost"/> and
/// <see cref="ISearch"/>. <c>Given</c> commits aggregates, <see cref="CatchUp"/> indexes them, <see cref="Search"/> asks.
/// </summary>
/// <example>
/// <code>
/// var test = SearchTest.Create(s => s.Add&lt;Artwork&gt;());
/// test.Given(Artwork.Create("Sunset over harbour", "oil on canvas", now), tenant);
/// await test.CatchUp();
/// var page = (await test.Find("harbour", tenant)).GetValueOrThrow();
/// </code>
/// </example>
public sealed class SearchTest
{
    private readonly Actor _actor = Actor.User("test-user");
    private readonly TimeProvider _time;

    private SearchTest(InMemoryAggregateStore store, SearchRegistry registry, ISearchIndex index, SearchTokens tokens, TimeProvider time)
    {
        _time = time;
        Store = store;
        Registry = registry;
        Index = index;
        Tokens = tokens;
        Host = new SearchHost(registry, index, store, store, tokens, time);
        Search = new SearchService(registry, index, tokens);
    }

    /// <summary>The store and its feed.</summary>
    public InMemoryAggregateStore Store { get; }

    /// <summary>The searchable aggregates.</summary>
    public SearchRegistry Registry { get; }

    /// <summary>The index.</summary>
    public ISearchIndex Index { get; }

    /// <summary>The blind tokens (over the store's key store when it has one).</summary>
    public SearchTokens Tokens { get; }

    /// <summary>The host.</summary>
    public SearchHost Host { get; }

    /// <summary>The application's search.</summary>
    public ISearch Search { get; }

    /// <summary>A test with a fresh store and an in-memory index (or <paramref name="index"/>).</summary>
    /// <param name="configure">Adds the aggregates.</param>
    /// <param name="index">The index under test; <see langword="null"/> = <see cref="InMemorySearchIndex"/>.</param>
    /// <param name="keys">A key store (sealing, blind tokens); <see langword="null"/> = none. Sealing needs <paramref name="codec"/>.</param>
    /// <param name="codec">The event codec (with the aggregates added) when <paramref name="keys"/> seals personal data.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The test.</returns>
    public static SearchTest Create(Action<SearchRegistry> configure, ISearchIndex? index = null, IKeyStore? keys = null, EventCodec? codec = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        time ??= TimeProvider.System;
        var registry = new SearchRegistry { Loaders = 1 };
        configure(registry);
        var store = new InMemoryAggregateStore(time, new InMemoryStoreOptions { Keys = keys, Codec = codec });
        return new SearchTest(store, registry, index ?? new InMemorySearchIndex(), new SearchTokens(keys), time);
    }

    /// <summary>Commits an aggregate's uncommitted events in a tenant.</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>This test.</returns>
    public SearchTest Given<T>(T aggregate, Option<Id<ITenant>> tenant = default)
        where T : AggregateRoot<T>, IAggregateRoot<T>
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (AggregateChanges.Prepare(aggregate).TryGetValue(out Violation? broken))
            throw new InvalidOperationException($"The given {typeof(T).Name} breaks an invariant: {broken}.");
        var expected = aggregate.Version - aggregate.UncommittedChanges.Length;
        var appended = Store.Append(aggregate, expected, Context(tenant), Option.None, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (appended.TryGetValue(out Violation? refused))
            throw new InvalidOperationException($"The store refused the given {typeof(T).Name}: {refused}.");
        AggregateChanges.Committed(aggregate);
        return this;
    }

    /// <summary>Indexes everything committed so far.</summary>
    /// <returns>The number of feed events read.</returns>
    public Task<long> CatchUp() => Host.CatchUp(CancellationToken.None);

    /// <summary>Searches in a tenant (as a system caller: every type, no audience filter, unless the query says otherwise).</summary>
    /// <param name="query">The query.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The page or the violation.</returns>
    public ValueTask<Result<SearchPage>> Find(SearchQuery query, Option<Id<ITenant>> tenant = default)
        => Search.Search(query, Context(tenant), CancellationToken.None);

    /// <summary>Searches a text in a tenant.</summary>
    /// <param name="text">The query text.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The page or the violation.</returns>
    public ValueTask<Result<SearchPage>> Find(string text, Option<Id<ITenant>> tenant = default) => Find(new SearchQuery(text), tenant);

    private OperationContext Context(Option<Id<ITenant>> tenant) => new(_actor, tenant, Id<ICorrelation>.New(_time), Option.None);
}
