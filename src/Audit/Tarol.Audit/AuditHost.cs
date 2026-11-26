using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tarol.Kernel;
using Tarol.Persistence;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.StronglyTypedId;

namespace Tarol.Audit;

/// <summary>
/// Feeds the change log from the event feed (TN-64, AU-1 option B): one row per event and changed field, the "before" from the last
/// value of the field, both written with the checkpoint (exactly once). Every member of every event of the audited aggregates is a
/// field (its JSON name) unless <c>[NotAudited]</c>; a <c>[PersonalData]</c> value is kept sealed under its subject's key, so it reads as
/// <c>null</c> once the subject is erased (crypto-shredding, AU-3). A new version rebuilds the rows from the feed.
/// </summary>
public sealed class AuditHost : ProjectionHost
{
    /// <summary>The projection name: <c>tarol-audit</c>.</summary>
    public const string ProjectionName = "tarol-audit";

    /// <summary>The meter of the row counter.</summary>
    public new const string MeterName = "Tarol.Audit";

    private const string RowScope = "tarol.audit.row";
    private static readonly Meter SharedMeter = new(MeterName);
    private readonly AuditRegistry _registry;
    private readonly IAuditStore _store;
    private readonly EventCodec _codec;
    private readonly IKeyStore? _keys;
    private readonly HashSet<string> _streamTypes;
    private readonly Counter<long> _rows;

    /// <summary>Creates the host.</summary>
    /// <param name="registry">The audited aggregates.</param>
    /// <param name="store">The log's store.</param>
    /// <param name="feed">The event feed.</param>
    /// <param name="codec">The event codec (the aggregates must be registered).</param>
    /// <param name="keys">The key store, when the application seals personal data.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    /// <param name="signal">Wakes the host after a commit.</param>
    /// <param name="gate">Lets one process run the host.</param>
    /// <param name="meters">Creates the meters.</param>
    public AuditHost(AuditRegistry registry, IAuditStore store, IEventFeed feed, EventCodec codec, IKeyStore? keys = null, TimeProvider? time = null,
        ILogger<AuditHost>? logger = null, IFeedSignal? signal = null, IProjectionGate? gate = null, IMeterFactory? meters = null)
        : base(ProjectionName, NotNull(registry).Version, feed, registry.Projection, time, logger, signal, gate, meters)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codec);
        _registry = registry;
        _store = store;
        _codec = codec;
        _keys = keys;
        _streamTypes = new HashSet<string>(registry.StreamTypes, StringComparer.Ordinal);
        _rows = (meters?.Create(MeterName) ?? SharedMeter).CreateCounter<long>("tarol.audit.rows", "{row}", "Change log rows written from the feed.");
    }

    /// <summary>The log's store.</summary>
    public IAuditStore Store => _store;

    /// <summary>The projection version the log reads.</summary>
    public int LogVersion => _registry.Version;

    private static AuditRegistry NotNull(AuditRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry;
    }

    /// <inheritdoc />
    protected override ValueTask Prepare(CancellationToken cancellationToken) => _store.Prepare(_registry.Version, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask<ProjectionCheckpoint> LoadCheckpoint(CancellationToken cancellationToken) => _store.Checkpoint(_registry.Version, cancellationToken);

    /// <inheritdoc />
    protected override async ValueTask<(bool Written, int Rows)> Write(IReadOnlyList<PositionedEvent> events, GlobalPosition from, GlobalPosition through,
        ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var mine = events.Where(e => _streamTypes.Contains(e.StreamType)).ToList();
        var objects = mine.Select(static e => (TenantOf(e.Event.Context), ObjectOf(e.Event))).Distinct().ToList();
        var known = objects.Count == 0
            ? new Dictionary<AuditValueKey, string?>()
            : new Dictionary<AuditValueKey, string?>(await _store.Values(_registry.Version, objects, cancellationToken).ConfigureAwait(false));
        var touched = new HashSet<(Guid, Guid)>(known.Keys.Select(static k => (k.Tenant, k.Target)));
        var entries = new List<AuditEntry>();
        var values = new Dictionary<AuditValueKey, string?>();
        var removed = new HashSet<(Guid, Guid)>();
        foreach (var e in mine)
            await Collect(e, known, touched, entries, values, removed, cancellationToken).ConfigureAwait(false);
        var written = await _store.Write(_registry.Version, new AuditBatch(entries, values, removed, from, through, state), cancellationToken).ConfigureAwait(false);
        if (written && entries.Count > 0)
            _rows.Add(entries.Count);
        return (written, entries.Count);
    }

    private async ValueTask Collect(PositionedEvent positioned, Dictionary<AuditValueKey, string?> known, HashSet<(Guid, Guid)> touched,
        List<AuditEntry> entries, Dictionary<AuditValueKey, string?> values, HashSet<(Guid, Guid)> removed, CancellationToken cancellationToken)
    {
        var recorded = positioned.Event;
        if (!_codec.TryDescribe(positioned.StreamType, recorded.Event.Event, out var entry) || entry.Audit is { Excluded: true })
            return;
        var tenant = TenantOf(recorded.Context);
        var obj = ObjectOf(recorded);
        var (plain, stored) = await Stored(positioned.StreamType, entry, recorded, tenant, cancellationToken).ConfigureAwait(false);
        var capture = recorded.Context.Capture;
        var row = new AuditEntry(Guid.Empty, tenant, recorded.RecordedAt, AuditKind.Changed, recorded.Context.Actor, positioned.StreamType, obj, null, null, null)
        {
            Stream = recorded.StreamId,
            Event = entry.Type.Alias,
            CapturedAt = capture?.At,
            Device = capture?.Device,
        };

        if (entry.Audit is { Removed: true })
        {
            entries.Add(row with { Id = RowId(recorded.EventId, ""), Kind = AuditKind.Removed });
            foreach (var key in known.Keys.Where(k => k.Tenant == tenant && k.Target == obj).ToList())
                known.Remove(key);
            foreach (var key in values.Keys.Where(k => k.Tenant == tenant && k.Target == obj).ToList())
                values.Remove(key);
            removed.Add((tenant, obj));
            touched.Remove((tenant, obj));
            return;
        }

        var skip = Skipped(entry.Audit);
        var isNew = touched.Add((tenant, obj));
        using var plainDocument = JsonDocument.Parse(plain);
        using var storedDocument = stored is null ? null : JsonDocument.Parse(stored);
        if (entry.Audit?.Time is { } time && plainDocument.RootElement.TryGetProperty(JsonName(time), out var device)
            && device.ValueKind == JsonValueKind.String && device.TryGetDateTimeOffset(out var at))
            row = row with { CapturedAt = at };
        foreach (var property in plainDocument.RootElement.EnumerateObject())
        {
            if (skip.Contains(property.Name))
                continue;
            var plainAfter = Text(property.Value);
            var after = storedDocument is not null && storedDocument.RootElement.TryGetProperty(property.Name, out var sealedValue)
                ? Text(sealedValue)
                : plainAfter;
            var key = new AuditValueKey(tenant, obj, property.Name);
            var had = known.TryGetValue(key, out var before);
            // compare in plain text: a sealed value is sealed with a fresh nonce each time
            if (had && string.Equals(await Plain(before, tenant, cancellationToken).ConfigureAwait(false), plainAfter, StringComparison.Ordinal))
                continue;                                                                       // the same value: no row
            if (!had && plainAfter is null && !isNew)
                continue;                                                                       // null on a field never set
            known[key] = after;
            values[key] = after;
            entries.Add(row with
            {
                Id = RowId(recorded.EventId, property.Name),
                Kind = isNew ? AuditKind.Created : AuditKind.Changed,
                Field = property.Name,
                Before = had ? before : null,
                After = after,
            });
        }
    }

    // a value as the log keeps it: JSON, a string without escaping of non-ASCII letters (a person reads the log), null as null
    private static string? Text(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                    writer.WriteStringValue(value.GetString());
                return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
            default:
                return value.GetRawText();
        }
    }

    // a stored value in plain text: an envelope opened with its subject's key (an erased one reads as null)
    private async ValueTask<string?> Plain(string? stored, Guid tenant, CancellationToken cancellationToken)
    {
        if (stored is null)
            return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(stored);
        if (!EventCodec.IsSealed(bytes))
            return stored;
        var subjects = new List<Guid>();
        EventCodec.SealedSubjects(bytes, subjects);
        var ring = _keys is null ? null : await _keys.Keys(tenant, subjects, create: false, cancellationToken).ConfigureAwait(false);
        var opened = new ArrayBufferWriter<byte>(bytes.Length);
        EventCodec.OpenState(bytes, ring, opened);
        var text = System.Text.Encoding.UTF8.GetString(opened.WrittenSpan);
        return text == "null" ? null : text;
    }

    // the payload plain, and as stored when it holds personal data: sealed under its subjects' keys (an erased subject's as null)
    private async ValueTask<(byte[] Plain, byte[]? Stored)> Stored(string streamType, EventCatalogEntry entry, RecordedEvent recorded, Guid tenant,
        CancellationToken cancellationToken)
    {
        var plain = new ArrayBufferWriter<byte>(256);
        var type = _codec.Encode(streamType, recorded.Event.Event, plain);
        var bytes = plain.WrittenSpan.ToArray();
        if (entry.Personal is null || _keys is null)
            return (bytes, null);
        var subjects = new List<Guid>();
        _codec.SubjectsOf(streamType, type, bytes, recorded.StreamId, subjects);
        if (subjects.Count == 0)
            return (bytes, null);
        var ring = await _keys.Keys(tenant, [.. subjects.Distinct()], create: false, cancellationToken).ConfigureAwait(false);
        var sealedPayload = new ArrayBufferWriter<byte>(bytes.Length + 128);
        return _codec.SealStored(streamType, type, bytes, recorded.StreamId, ring, sealedPayload) ? (bytes, sealedPayload.WrittenSpan.ToArray()) : (bytes, null);
    }

    private HashSet<string> Skipped(EventAudit? audit)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (audit is null)
            return skip;
        foreach (var member in audit.NotAudited)
            skip.Add(JsonName(member));
        if (audit.Time is { } time)
            skip.Add(JsonName(time));                                                           // the device time is the row's, not a field
        return skip;
    }

    private string JsonName(AuditMember member) => member.JsonName ?? _codec.Options.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;

    private static Guid RowId(Guid eventId, string field) => Id<AuditEntry>.Derive(RowScope, eventId, field).Value;

    internal static Guid TenantOf(OperationContext context) => context.Tenant.TryGetValue(out Id<ITenant> tenant) ? tenant.Value : Guid.Empty;

    private static Guid ObjectOf(RecordedEvent recorded) => recorded.Event.EntityId == Guid.Empty ? recorded.StreamId : recorded.Event.EntityId;
}
