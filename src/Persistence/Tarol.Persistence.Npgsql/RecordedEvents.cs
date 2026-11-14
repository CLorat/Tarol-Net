using System.Globalization;
using Npgsql;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.Npgsql;

/// <summary>Reads a <see cref="RecordedEvent"/> from the event columns, for the feed and the outbox.</summary>
internal static class RecordedEvents
{
    /// <summary>The columns, in the order <see cref="Read"/> expects (prefix with a table alias where needed).</summary>
    public static string Columns(string alias) =>
        $"{alias}.tx_id::text AS tx_text, {alias}.position, {alias}.type, {alias}.stream_id, {alias}.version, {alias}.entity_id, {alias}.parent_id, "
        + $"{alias}.alias, {alias}.alias_version, {alias}.payload, {alias}.event_id, {alias}.recorded_at, {alias}.actor_kind, {alias}.actor_id, "
        + $"{alias}.on_behalf_of, {alias}.correlation_id, {alias}.causation_id, {alias}.tenant";

    /// <summary>The number of columns <see cref="Columns"/> lists.</summary>
    public const int Count = 18;

    public static PositionedEvent Read(NpgsqlDataReader reader, int offset, EventCodec codec)
    {
        var type = reader.GetString(offset + 2);
        var domainEvent = codec.Decode(type, new EventType(reader.GetString(offset + 7), reader.GetInt32(offset + 8)), reader.GetFieldValue<byte[]>(offset + 9));
        var tenant = reader.GetGuid(offset + 17);
        var actor = new Actor(Enum.Parse<ActorKind>(reader.GetString(offset + 12)), reader.GetString(offset + 13),
            reader.IsDBNull(offset + 14) ? Option.None : reader.GetString(offset + 14));
        var context = new OperationContext(actor, tenant == Guid.Empty ? Option.None : (Id<ITenant>)tenant,
            (Id<ICorrelation>)reader.GetGuid(offset + 15), reader.IsDBNull(offset + 16) ? Option.None : (Id<ICausation>)reader.GetGuid(offset + 16));
        var recorded = new RecordedEvent(reader.GetGuid(offset + 3),
            new StreamEvent(reader.GetInt64(offset + 4), reader.GetGuid(offset + 5), reader.IsDBNull(offset + 6) ? Guid.Empty : reader.GetGuid(offset + 6), domainEvent),
            reader.GetGuid(offset + 10), reader.GetFieldValue<DateTimeOffset>(offset + 11), context);
        return new PositionedEvent(
            new GlobalPosition(ulong.Parse(reader.GetString(offset), CultureInfo.InvariantCulture), reader.GetInt64(offset + 1)), type, recorded);
    }
}
