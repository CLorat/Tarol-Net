using System.Globalization;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>The columns of a recorded event and how they are read: raw first, then decoded per page with the keys it needs.</summary>
internal static class RecordedEvents
{
    public static string Columns(string alias) =>
        $"{alias}.tx_id::text AS tx_text, {alias}.position, {alias}.type, {alias}.stream_id, {alias}.version, {alias}.entity_id, {alias}.parent_id, "
        + $"{alias}.alias, {alias}.alias_version, {alias}.payload, {alias}.event_id, {alias}.recorded_at, {alias}.actor_kind, {alias}.actor_id, "
        + $"{alias}.on_behalf_of, {alias}.correlation_id, {alias}.causation_id, {alias}.tenant, {alias}.device, {alias}.captured_at";

    public const int Count = 20;

    /// <summary>A recorded event as read, its payload still stored (sealed fields sealed).</summary>
    public sealed record Raw(GlobalPosition Position, string StreamType, Guid Tenant, Guid StreamId, long Version, Guid EntityId, Guid ParentId,
        EventType Type, byte[] Payload, Guid EventId, DateTimeOffset RecordedAt, OperationContext Context);

    public static Raw ReadRaw(NpgsqlDataReader reader, int offset)
    {
        var tenant = reader.GetGuid(offset + 17);
        var actor = new Actor(Enum.Parse<ActorKind>(reader.GetString(offset + 12)), reader.GetString(offset + 13),
            reader.IsDBNull(offset + 14) ? Option.None : reader.GetString(offset + 14));
        var context = new OperationContext(actor, tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant,
            (Id<ICorrelation>)reader.GetGuid(offset + 15), reader.IsDBNull(offset + 16) ? Option.None : (Id<ICausation>)reader.GetGuid(offset + 16))
        {
            // TN-60b: the capture of an offline device, when the commit had one
            Capture = reader.IsDBNull(offset + 18) && reader.IsDBNull(offset + 19) ? null
                : new Capture(reader.IsDBNull(offset + 18) ? null : reader.GetString(offset + 18),
                    reader.IsDBNull(offset + 19) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 19)),
        };
        return new Raw(
            new GlobalPosition(ulong.Parse(reader.GetString(offset), CultureInfo.InvariantCulture), reader.GetInt64(offset + 1)),
            reader.GetString(offset + 2), tenant, reader.GetGuid(offset + 3), reader.GetInt64(offset + 4), reader.GetGuid(offset + 5),
            reader.IsDBNull(offset + 6) ? Guid.Empty : reader.GetGuid(offset + 6),
            new EventType(reader.GetString(offset + 7), reader.GetInt32(offset + 8)), reader.GetFieldValue<byte[]>(offset + 9),
            reader.GetGuid(offset + 10), reader.GetFieldValue<DateTimeOffset>(offset + 11), context);
    }

    /// <summary>Decodes a page: the keys of its sealed fields are fetched once per tenant (cache first).</summary>
    public static async ValueTask<PositionedEvent[]> Decode(IReadOnlyList<Raw> rows, EventCodec codec, IKeyStore? keys, CancellationToken cancellationToken)
    {
        var rings = await Rings(rows.Select(static r => (r.Tenant, r.Payload)), keys, cancellationToken).ConfigureAwait(false);
        var events = new PositionedEvent[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var domainEvent = rings.TryGetValue(row.Tenant, out var ring) && EventCodec.IsSealed(row.Payload)
                ? codec.Decode(row.StreamType, row.Type, row.Payload, ring)
                : codec.Decode(row.StreamType, row.Type, row.Payload);
            events[i] = new PositionedEvent(row.Position, row.StreamType,
                new RecordedEvent(row.StreamId, new StreamEvent(row.Version, row.EntityId, row.ParentId, domainEvent), row.EventId, row.RecordedAt, row.Context));
        }

        return events;
    }

    /// <summary>One key ring per tenant for the sealed subjects of <paramref name="payloads"/>; empty without a key store.</summary>
    public static async ValueTask<Dictionary<Guid, KeyRing>> Rings(IEnumerable<(Guid Tenant, byte[] Payload)> payloads, IKeyStore? keys, CancellationToken cancellationToken)
    {
        var rings = new Dictionary<Guid, KeyRing>();
        if (keys is null)
            return rings;
        var subjects = new Dictionary<Guid, List<Guid>>();
        foreach (var (tenant, payload) in payloads)
        {
            if (!EventCodec.IsSealed(payload))
                continue;
            if (!subjects.TryGetValue(tenant, out var list))
                subjects[tenant] = list = [];
            EventCodec.SealedSubjects(payload, list);
        }

        foreach (var (tenant, list) in subjects)
            rings[tenant] = await keys.Keys(tenant, list, create: false, cancellationToken).ConfigureAwait(false);
        return rings;
    }
}
