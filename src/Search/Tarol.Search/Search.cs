using System.Diagnostics;
using System.Diagnostics.Metrics;
using Tarol.Kernel;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Search;

/// <summary>A search as an application asks it; the tenant comes from the <see cref="OperationContext"/>, never from here.</summary>
/// <param name="Text">The query in web-search syntax (words, <c>"a phrase"</c>, <c>-word</c>, <c>OR</c>).</param>
public sealed record SearchQuery(string Text)
{
    /// <summary>The document types to search; <see langword="null"/> = every registered type the caller may see.</summary>
    public IReadOnlyCollection<string>? Types { get; init; }

    /// <summary>The caller's audience keys (subject, roles); <see langword="null"/> = no audience filter (system callers).</summary>
    public IReadOnlyCollection<string>? Audience { get; init; }

    /// <summary>The last word is a prefix.</summary>
    public bool Prefix { get; init; }

    /// <summary>Count matches per type.</summary>
    public bool Counts { get; init; }

    /// <summary>Return snippets.</summary>
    public bool Snippets { get; init; }

    /// <summary>The page size.</summary>
    public int Limit { get; init; } = SearchLimits.DefaultLimit;

    /// <summary>The cursor of the previous page.</summary>
    public string? After { get; init; }

    /// <summary>The query's language (ISO 639-1).</summary>
    public string? Language { get; init; }
}

/// <summary>Searches the index for the tenant of the caller's context (registered by <c>AddSearch</c>).</summary>
public interface ISearch
{
    /// <summary>The registered document types.</summary>
    IReadOnlyList<SearchType> Types { get; }

    /// <summary>A page of hits in the context's tenant.</summary>
    /// <param name="query">The query.</param>
    /// <param name="context">The caller's context: its tenant scopes the search.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The page, or <see cref="Invalid"/> for a bad limit, text, type or cursor.</returns>
    ValueTask<Result<SearchPage>> Search(SearchQuery query, OperationContext context, CancellationToken cancellationToken);
}

/// <summary>The <see cref="ISearch"/> of <c>AddSearch</c>: the tenant from the context, the exact token, the meter.</summary>
public sealed class SearchService : ISearch
{
    private static readonly Meter SharedMeter = new(SearchHost.MeterName);
    private readonly ISearchIndex _index;
    private readonly SearchTokens _tokens;
    private readonly HashSet<string> _types;
    private readonly Counter<long> _queries;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the service.</summary>
    /// <param name="registry">The searchable aggregates.</param>
    /// <param name="index">The index.</param>
    /// <param name="tokens">Blinds the query for exact values.</param>
    /// <param name="meters">Creates the meter <see cref="SearchHost.MeterName"/>.</param>
    public SearchService(SearchRegistry registry, ISearchIndex index, SearchTokens? tokens = null, IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _tokens = tokens ?? new SearchTokens();
        Types = registry.Types;
        _types = [.. Types.Select(static t => t.Type)];
        var meter = meters?.Create(SearchHost.MeterName) ?? SharedMeter;
        _queries = meter.CreateCounter<long>("tarol.search.queries", "{query}", "Searches, by outcome (ok, invalid, error).");
        _duration = meter.CreateHistogram<double>("tarol.search.duration", "s", "Duration of a search.");
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchType> Types { get; }

    /// <inheritdoc />
    public async ValueTask<Result<SearchPage>> Search(SearchQuery query, OperationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var started = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            if (query.Types?.FirstOrDefault(t => !_types.Contains(t)) is { } unknown)
            {
                outcome = "invalid";
                return Invalid.Field("types", "Search.Type", $"'{unknown}' is not a searchable type.");
            }
            var tenant = context.Tenant.TryGetValue(out Id<ITenant> t) ? t.Value : Guid.Empty;
            var request = new SearchRequest(tenant, query.Text ?? "")
            {
                Types = query.Types ?? (IReadOnlyCollection<string>)_types,
                Audience = query.Audience,
                Prefix = query.Prefix,
                Counts = query.Counts,
                Snippets = query.Snippets,
                Limit = query.Limit,
                After = query.After,
                Language = query.Language,
                ExactToken = query.Text is { Length: > 0 and <= SearchLimits.MaxText }
                    ? await _tokens.Token(tenant, query.Text, create: false, cancellationToken).ConfigureAwait(false)
                    : null,
            };
            var page = await _index.Search(request, cancellationToken).ConfigureAwait(false);
            outcome = page.IsSuccess ? "ok" : "invalid";
            return page;
        }
        finally
        {
            var tags = new TagList { { "tarol.outcome", outcome } };
            _queries.Add(1, tags);
            _duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
