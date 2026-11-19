using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Search;

/// <summary>The searchable aggregates of an application and the index version (<c>AddSearch</c>).</summary>
public sealed class SearchRegistry
{
    private readonly Dictionary<string, SearchSource> _sources = new(StringComparer.Ordinal);

    /// <summary>The index version (GS-5): raise it when the searchable members change; the generated fingerprint refuses a forgotten bump.</summary>
    public int Version { get; set; } = 1;

    /// <summary>How many streams of a batch are loaded at once (default 4: 3,315 streams/s in the design probe).</summary>
    public int Loaders { get; set; } = 4;

    /// <summary>Batch, poll and backoff of the host.</summary>
    public Tarol.Queries.ProjectionOptions Projection { get; } = new();

    /// <summary>Makes <typeparamref name="T"/> searchable.</summary>
    /// <typeparam name="T">A <c>[Searchable]</c> aggregate (the generator implements <see cref="ISearchable{T}"/>).</typeparam>
    /// <returns>This registry.</returns>
    /// <exception cref="InvalidOperationException">The aggregate is added twice.</exception>
    public SearchRegistry Add<T>()
        where T : AggregateRoot<T>, IAggregateRoot<T>, ISearchable<T>
    {
        if (!_sources.TryAdd(T.StreamType, new SearchSource<T>()))
            throw new InvalidOperationException($"The searchable aggregate '{T.StreamType}' is added twice.");
        return this;
    }

    /// <summary>The registered aggregates' sources by stream type.</summary>
    internal IReadOnlyDictionary<string, SearchSource> Sources => _sources;

    /// <summary>Every document type, ordered.</summary>
    public IReadOnlyList<SearchType> Types => [.. _sources.Values.SelectMany(static s => s.Shape.Types).OrderBy(static t => t.Type, StringComparer.Ordinal)];

    /// <summary>
    /// The document types the caller may read (GS-7): every type of an aggregate whose read <paramref name="access"/> grants
    /// (an entity type follows its aggregate). The HTTP entry asks <c>CheckList&lt;T&gt;</c> (<c>Read&lt;T&gt;</c>) per aggregate.
    /// </summary>
    /// <param name="access">Answers whether the caller may read an aggregate type.</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    /// <returns>The readable document types, ordered.</returns>
    public async ValueTask<IReadOnlyList<string>> Readable(ISearchAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var readable = new List<string>();
        foreach (var source in _sources.Values)
            if (await source.CanRead(access, cancellationToken).ConfigureAwait(false))
                readable.AddRange(source.Shape.Types.Select(static t => t.Type));
        readable.Sort(StringComparer.Ordinal);
        return readable;
    }

    /// <summary>The definition the index is prepared with: the version and the combined fingerprint of every shape.</summary>
    public SearchIndexDefinition Definition()
    {
        var sb = new StringBuilder();
        foreach (var source in _sources.Values.OrderBy(static s => s.Shape.StreamType, StringComparer.Ordinal))
            sb.Append(source.Shape.StreamType).Append('=').Append(source.Shape.Fingerprint).Append(';');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return new SearchIndexDefinition(Version, Convert.ToHexStringLower(hash.AsSpan(0, 8)), Types);
    }
}

/// <summary>Whether the caller may read an aggregate type (the HTTP entry implements it with <c>CheckList&lt;T&gt;</c>).</summary>
public interface ISearchAccess
{
    /// <summary>Whether the caller may read <typeparamref name="T"/> (<c>Read&lt;T&gt;</c>).</summary>
    /// <typeparam name="T">The aggregate.</typeparam>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether it may.</returns>
    ValueTask<bool> CanRead<T>(CancellationToken cancellationToken)
        where T : AggregateRoot<T>, IAggregateRoot<T>;
}

/// <summary>Loads one searchable aggregate type and maps it to documents.</summary>
internal abstract class SearchSource
{
    public abstract SearchShape Shape { get; }

    public abstract ValueTask<bool> CanRead(ISearchAccess access, CancellationToken cancellationToken);

    /// <summary>The stream's documents (none when the stream is gone) and its version.</summary>
    public abstract ValueTask<(long Version, List<SearchDocument> Documents)> Load(IAggregateReader reader, Guid tenant, Guid id, TimeProvider time,
        CancellationToken cancellationToken);
}

internal sealed class SearchSource<T> : SearchSource
    where T : AggregateRoot<T>, IAggregateRoot<T>, ISearchable<T>
{
    private static readonly Actor Reader = Actor.System("tarol.search");

    public override SearchShape Shape => T.Shape;

    public override ValueTask<bool> CanRead(ISearchAccess access, CancellationToken cancellationToken) => access.CanRead<T>(cancellationToken);

    public override async ValueTask<(long Version, List<SearchDocument> Documents)> Load(IAggregateReader reader, Guid tenant, Guid id, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var context = OperationContext.Start(Reader, tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant, time);
        var loaded = await reader.Load((Id<T>)id, context, cancellationToken).ConfigureAwait(false);
        var documents = new List<SearchDocument>();
        if (!loaded.TryGetValue(out T? aggregate))
            return (0, documents);
        T.Documents(aggregate, documents);
        return (aggregate.Version, documents);
    }
}

/// <summary>
/// The blind tokens of exact values (GS-8): HMAC under the tenant's lookup key (<see cref="IKeyStore.BlindIndex"/>) when a key
/// store is registered; without one an unkeyed SHA-256 (the value is still never stored as text, and the guide says to add a key
/// store before marking personal members).
/// </summary>
public sealed class SearchTokens
{
    /// <summary>The lookup name the tokens are derived under.</summary>
    public const string Lookup = "tarol.search";

    private readonly IKeyStore? _keys;

    /// <summary>Creates the tokens.</summary>
    /// <param name="keys">The key store, or <see langword="null"/>.</param>
    public SearchTokens(IKeyStore? keys = null) => _keys = keys;

    /// <summary>The token of a value, or <see langword="null"/> when the tenant is erased (or has no key and <paramref name="create"/> is off).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="value">The value (normalised here).</param>
    /// <param name="create">Create a missing tenant key (the feeder) or not (a search).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token.</returns>
    public async ValueTask<string?> Token(Guid tenant, string value, bool create, CancellationToken cancellationToken)
    {
        var normalized = SearchText.Normalize(value);
        if (normalized.Length == 0)
            return null;
        if (_keys is not null)
            return await _keys.BlindIndex(tenant, Lookup, normalized, create, cancellationToken).ConfigureAwait(false);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenant.ToString("N", CultureInfo.InvariantCulture) + "|" + normalized));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>Loads streams and turns them into stored documents (exact values blinded); shared by the host and the eraser.</summary>
internal sealed class SearchCollector(SearchRegistry registry, IAggregateReader reader, SearchTokens tokens, TimeProvider time)
{
    public SearchRegistry Registry => registry;

    public bool Covers(string streamType) => registry.Sources.ContainsKey(streamType);

    public async Task<IReadOnlyList<StreamDocuments>> Collect(IReadOnlyList<(Guid Tenant, string StreamType, Guid Id)> streams, CancellationToken cancellationToken)
    {
        var result = new StreamDocuments[streams.Count];
        await Parallel.ForAsync(0, streams.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, registry.Loaders), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var (tenant, type, id) = streams[i];
                var (version, documents) = await registry.Sources[type].Load(reader, tenant, id, time, ct).ConfigureAwait(false);
                for (var d = 0; d < documents.Count; d++)
                {
                    if (documents[d].Exact.Count == 0)
                        continue;
                    var blinded = new List<string>(documents[d].Exact.Count);
                    foreach (var value in documents[d].Exact)
                        if (await tokens.Token(tenant, value, create: true, ct).ConfigureAwait(false) is { } token)
                            blinded.Add(token);
                    documents[d] = documents[d] with { Exact = blinded };
                }
                result[i] = new StreamDocuments(tenant, id, version, documents);
            }).ConfigureAwait(false);
        return result;
    }
}
