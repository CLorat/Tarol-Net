using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tarol.Changes;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Results;

namespace Tarol.Messaging;

/// <summary>
/// On a static mapping method: when <typeparamref name="TEvent"/> commits, publish it as <c>contract</c> in
/// <c>version</c> (docs/architecture/messaging.md §4, M-7). The method is the outward anti-corruption layer: it maps the
/// domain event to the contract record and does nothing else; the domain event is never the contract.
/// </summary>
/// <typeparam name="TEvent">The committed event.</typeparam>
/// <example>
/// <code>
/// public static class Publications
/// {
///     [Publishes&lt;Auction.Settled&gt;("artflow.auction-settled", 1)]
///     public static AuctionSettledV1 SettledV1(Auction.Settled e, RecordedEvent r) => new(r.StreamId, e.Buyer, e.Price, "EUR");
/// }
/// </code>
/// </example>
/// <remarks>
/// The method is <c>public static</c> (or <c>internal</c>), takes <c>(TEvent)</c> or <c>(TEvent, RecordedEvent)</c> and returns
/// a class, the contract record; returning <see langword="null"/> publishes nothing for that event (TMSG004). One method
/// per (contract, version) in an assembly (TMSG005). The store writes one outbox row
/// <c>publish:&lt;contract&gt;@&lt;version&gt;</c> per publication in the commit (M-10); the reaction worker maps and publishes
/// it, at least once, in version order per stream. The message id is the event id.
/// </remarks>
/// <param name="contract">The contract, prefixed by this system, e.g. <c>artflow.auction-settled</c>.</param>
/// <param name="version">The contract version, 1 or more.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PublishesAttribute<TEvent>(string contract, int version) : Attribute
    where TEvent : DomainEvent
{
    /// <summary>The contract.</summary>
    public string Contract { get; } = contract;

    /// <summary>The contract version.</summary>
    public int Version { get; } = version;
}

/// <summary>One publication: its outbox row name, contract and version.</summary>
/// <param name="Name">The row name, <c>publish:&lt;contract&gt;@&lt;version&gt;</c> (<see cref="NameOf"/>).</param>
/// <param name="Contract">The contract.</param>
/// <param name="Version">The contract version.</param>
/// <remarks>
/// The generated set (<c>GeneratedPublications</c>) also states the contract record, the event and the event's stream, so a
/// host can describe what it publishes (BC-25: <c>x-tarol-topics</c> in the OpenAPI document of <c>Tarol.Messaging.SignalR</c>).
/// </remarks>
public sealed record PublicationInfo(string Name, string Contract, int Version)
{
    /// <summary>The contract record the mapping method returns, e.g. <c>BidPlacedV1</c>; <see langword="null"/> when not stated.</summary>
    public Type? ContractType { get; init; }

    /// <summary>The domain event published, e.g. <c>Bid.Placed</c>; <see langword="null"/> when not stated.</summary>
    public Type? EventType { get; init; }

    /// <summary>
    /// The simple name of the aggregate whose stream the event belongs to, e.g. <c>Auction</c> for <c>Bid.Placed</c> of
    /// <c>Bid : Entity&lt;Auction, Bid&gt;</c>: the message's subject is that aggregate's id. <see langword="null"/> when the
    /// generator cannot know it (an event that derives from neither <c>AggregateRoot&lt;T&gt;.Event</c> nor an entity's events).
    /// </summary>
    public string? Stream { get; init; }

    /// <summary>The row name of a publication: <c>publish:&lt;contract&gt;@&lt;version&gt;</c>.</summary>
    /// <param name="contract">The contract.</param>
    /// <param name="version">The contract version.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string contract, int version)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return "publish:" + contract + "@" + version.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Maps and serializes the event of a row for a publication; <see langword="null"/> = this event is not published.</summary>
/// <param name="publication">The row name.</param>
/// <param name="recorded">The committed event.</param>
/// <param name="json">The application's JSON options (its source-generated context lists the contract records, TMSG006).</param>
/// <returns>The contract's JSON, or <see langword="null"/>.</returns>
public delegate byte[]? PublicationMapper(string publication, RecordedEvent recorded, JsonSerializerOptions json);

/// <summary>
/// The publications of an application: each assembly with <see cref="PublishesAttribute{TEvent}"/> methods has a generated
/// <c>GeneratedPublications.Set</c>; <see cref="Combine"/> joins several. It is the store's table of publication rows;
/// <see cref="MessagePublisher"/> runs them.
/// </summary>
public sealed class PublicationSet : IOutboxReactions
{
    private readonly Dictionary<string, PublicationInfo> _byName;
    private readonly Dictionary<string, PublicationMapper> _mappers;
    private readonly ReactionTable[] _tables;

    /// <summary>Creates the set of one assembly. Called by generated code.</summary>
    /// <param name="publications">The publications.</param>
    /// <param name="table">The publication rows per event (static arrays).</param>
    /// <param name="mapper">The map (row name, event) → contract JSON.</param>
    /// <exception cref="ArgumentException">A name is listed twice or does not match its contract and version.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PublicationSet(IReadOnlyList<PublicationInfo> publications, ReactionTable table, PublicationMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(mapper);
        _byName = new(StringComparer.Ordinal);
        _mappers = new(StringComparer.Ordinal);
        foreach (var publication in publications)
        {
            ArgumentNullException.ThrowIfNull(publication, nameof(publications));
            if (publication.Name != PublicationInfo.NameOf(publication.Contract, publication.Version) || Message.SystemOf(publication.Contract).Length == 0)
                throw new ArgumentException($"The publication '{publication.Name}' is not publish:<system>.<name>@<version>.", nameof(publications));
            if (!_byName.TryAdd(publication.Name, publication))
                throw new ArgumentException($"The publication '{publication.Name}' is listed twice.", nameof(publications));
            _mappers[publication.Name] = mapper;
        }
        _tables = [table];
        Publications = [.. publications];
    }

    private PublicationSet(Dictionary<string, PublicationInfo> byName, Dictionary<string, PublicationMapper> mappers, ReactionTable[] tables)
    {
        _byName = byName;
        _mappers = mappers;
        _tables = tables;
        Publications = [.. byName.Values];
    }

    /// <summary>A set without publications.</summary>
    public static PublicationSet Empty { get; } = new([], static _ => ReadOnlySpan<string>.Empty, static (_, _, _) => null);

    /// <summary>The publications.</summary>
    public IReadOnlyList<PublicationInfo> Publications { get; }

    /// <summary>The row names.</summary>
    public IReadOnlyList<string> Names => [.. Publications.Select(static p => p.Name)];

    /// <summary>The contracts published (each once), e.g. for <see cref="IMessageTransport.Prepare"/>.</summary>
    public IReadOnlyList<string> Topics => [.. Publications.Select(static p => p.Contract).Distinct(StringComparer.Ordinal)];

    /// <summary>Joins the sets of several assemblies.</summary>
    /// <param name="sets">The sets.</param>
    /// <returns>The combined set.</returns>
    /// <exception cref="InvalidOperationException">A (contract, version) is published by two sets.</exception>
    public static PublicationSet Combine(params PublicationSet[] sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var byName = new Dictionary<string, PublicationInfo>(StringComparer.Ordinal);
        var mappers = new Dictionary<string, PublicationMapper>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set, nameof(sets));
            foreach (var (name, info) in set._byName)
            {
                if (!byName.TryAdd(name, info))
                    throw new InvalidOperationException($"The contract {info.Contract} v{info.Version} is published by two assemblies; one publication per contract version.");
                mappers[name] = set._mappers[name];
            }
        }
        return new(byName, mappers, [.. sets.SelectMany(static s => s._tables)]);
    }

    /// <summary>The publication of a row name, or <see langword="null"/>.</summary>
    /// <param name="name">The row name.</param>
    /// <returns>The publication.</returns>
    public PublicationInfo? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>Maps <paramref name="recorded"/> for the publication <paramref name="name"/>.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="recorded">The committed event.</param>
    /// <param name="json">The application's JSON options.</param>
    /// <returns>The contract's JSON; <see langword="null"/> when the mapping method returned <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">No publication has that name, or the JSON options do not know its record.</exception>
    public byte[]? Map(string name, RecordedEvent recorded, JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(json);
        return _mappers.TryGetValue(name, out var mapper)
            ? mapper(name, recorded, json)
            : throw new InvalidOperationException($"No publication named '{name}' is registered (removed, or its contract version changed).");
    }

    /// <summary>Serializes a contract record through the application's JSON (source-generated metadata; called by generated code).</summary>
    /// <typeparam name="T">The contract record.</typeparam>
    /// <param name="value">The record; <see langword="null"/> = not published.</param>
    /// <param name="json">The application's JSON options.</param>
    /// <returns>The JSON, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The JSON options have no metadata for <typeparamref name="T"/> (TMSG006).</exception>
    public static byte[]? Serialize<T>(T? value, JsonSerializerOptions json)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        if (value is null)
            return null;
        if (json.TypeInfoResolver?.GetTypeInfo(typeof(T), json) is not JsonTypeInfo<T> info)
            throw new InvalidOperationException($"The application's JSON context has no metadata for {typeof(T).Name}: add [JsonSerializable(typeof({typeof(T).Name}))].");
        return JsonSerializer.SerializeToUtf8Bytes(value, info);
    }

    /// <inheritdoc />
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent)
    {
        if (_tables.Length == 1)
            return _tables[0](domainEvent);
        List<string>? all = null;
        foreach (var table in _tables)
        {
            var names = table(domainEvent);
            if (names.IsEmpty)
                continue;
            all ??= [];
            foreach (var name in names)
                all.Add(name);
        }
        return all is null ? ReadOnlySpan<string>.Empty : all.ToArray();
    }

    /// <inheritdoc />
    /// <remarks>Publications leave at the commit.</remarks>
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => null;

    /// <inheritdoc />
    /// <remarks>Publications are never scheduled: one stream's messages leave in version order.</remarks>
    public bool IsScheduled(string reaction) => false;
}

/// <summary>
/// Runs the publication rows (M-10) on the reaction worker: map the event (the generated map through the application's
/// JSON context), build the envelope (<see cref="Message.Of(PositionedEvent, string, int, ReadOnlyMemory{byte}, string?)"/>: message id = event id, <c>traceparent</c> = the worker's
/// <c>publish &lt;contract&gt;</c> activity) and publish it. A row completes once the transport holds the message durably; a
/// failure is a retry with the worker's backoff, then <c>outbox_dead</c>.
/// </summary>
/// <remarks>
/// A crash after publishing and before the row completes publishes again: transports drop the duplicate by
/// <see cref="Message.DedupId"/> or deliver it and the consumer's key replays it, so a duplicate is a success. Before the
/// first publish the publisher declares its topics (<see cref="IMessageTransport.Prepare"/>), and again after a failure.
/// </remarks>
public sealed partial class MessagePublisher : IOutboxRowHandler
{
    private readonly PublicationSet _set;
    private readonly IMessageTransport _transport;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Counter<long> _published;
    private Task? _prepared;

    /// <summary>Creates the publisher.</summary>
    /// <param name="set">The publications (<c>GeneratedPublications.Set</c>, or <see cref="PublicationSet.Combine"/>).</param>
    /// <param name="transport">The transport.</param>
    /// <param name="json">The application's JSON options (its source-generated context lists the contract records).</param>
    /// <param name="logger">Logs skipped publications; <see langword="null"/> = none.</param>
    /// <param name="meters">Creates the meter <see cref="MessageConsumers.MeterName"/>; <see langword="null"/> = a shared meter.</param>
    public MessagePublisher(PublicationSet set, IMessageTransport transport, JsonSerializerOptions json, ILogger? logger = null, IMeterFactory? meters = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(json);
        _set = set;
        _transport = transport;
        _json = json;
        _log = logger ?? NullLogger.Instance;
        _published = (meters?.Create(MessageConsumers.MeterName) ?? MessageConsumers.SharedMeter)
            .CreateCounter<long>("tarol.messages.published", "{message}", "Publications handed to the transport, by contract and outcome (published, skipped, failed).");
    }

    /// <summary>The publications.</summary>
    public PublicationSet Publications => _set;

    /// <inheritdoc />
    public IReadOnlyList<string> Names => _set.Names;

    internal Action<Message>? Published { get; set; }

    /// <inheritdoc />
    public ReadOnlySpan<string> ReactionsTo(DomainEvent domainEvent) => _set.ReactionsTo(domainEvent);

    /// <inheritdoc />
    public DateTimeOffset? DueAt(DomainEvent domainEvent, string reaction) => null;

    /// <inheritdoc />
    public bool IsScheduled(string reaction) => false;

    /// <inheritdoc />
    public string ActivityName(string reaction) => _set.Find(reaction) is { } info ? "publish " + info.Contract : "publish " + reaction;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No publication has that name.</exception>
    /// <remarks>Without the feed position the message has no <see cref="Message.Position"/>; the reaction worker calls <see cref="RunPositioned"/>.</remarks>
    public ValueTask<Result<ChangeOutcome>> Run(ChangePipeline pipeline, string reaction, RecordedEvent recorded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        return Publish(reaction, recorded, null, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No publication has that name.</exception>
    /// <remarks>The message carries the event's position (<see cref="Message.Position"/>, <c>ce-position</c>).</remarks>
    public ValueTask<Result<ChangeOutcome>> RunPositioned(ChangePipeline pipeline, string reaction, PositionedEvent positioned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(positioned);
        return Publish(reaction, positioned.Event, positioned, cancellationToken);
    }

    private async ValueTask<Result<ChangeOutcome>> Publish(string reaction, RecordedEvent recorded, PositionedEvent? positioned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        var info = _set.Find(reaction)
            ?? throw new InvalidOperationException($"No publication named '{reaction}' is registered (removed, or its contract version changed).");
        var done = new ChangeOutcome(recorded.StreamId, recorded.Event.Version, 0);
        if (_set.Map(reaction, recorded, _json) is not { } data)
        {
            NotPublished(_log, info.Contract, info.Version, recorded.EventId);
            _published.Add(1, new TagList { { "tarol.contract", info.Contract }, { "tarol.outcome", "skipped" } });
            return done;
        }
        var activity = Activity.Current;
        var traceparent = activity is { IdFormat: ActivityIdFormat.W3C } ? activity.Id : null;
        var message = positioned is null
            ? Message.Of(recorded, info.Contract, info.Version, data, traceparent)
            : Message.Of(positioned, info.Contract, info.Version, data, traceparent);
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.destination.name", info.Contract);
        activity?.SetTag("tarol.contract.version", info.Version);
        await Prepare(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.Publish(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_gate)
                _prepared = null;                                                   // a lost topology is declared again
            _published.Add(1, new TagList { { "tarol.contract", info.Contract }, { "tarol.outcome", "failed" } });
            throw;                                                                  // the worker retries the row
        }
        _published.Add(1, new TagList { { "tarol.contract", info.Contract }, { "tarol.outcome", "published" } });
        Published?.Invoke(message);
        return done;
    }

    private async Task Prepare(CancellationToken cancellationToken)
    {
        Task prepared;
        lock (_gate)
            prepared = _prepared ??= _transport.Prepare(_set.Topics, [], CancellationToken.None);
        try
        {
            await prepared.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_gate)
                if (ReferenceEquals(_prepared, prepared))
                    _prepared = null;                                               // the next row declares again
            throw;
        }
    }

    [LoggerMessage(EventId = 11010, Level = LogLevel.Debug, Message = "Publication {Contract} v{Version} mapped event {EventId} to nothing; not published")]
    private static partial void NotPublished(ILogger logger, string contract, int version, Guid eventId);
}
