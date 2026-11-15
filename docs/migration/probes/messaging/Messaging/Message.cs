using System.Globalization;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>
/// One message between systems: CloudEvents 1.0 in <b>binary content mode</b>. The attributes travel as headers
/// (<c>ce-*</c>), the data is the contract's JSON. Candidate E-A; E-B (an own JSON envelope around the data) is the
/// webhook shape <c>{id, type, version, data}</c>.
/// </summary>
public sealed record Message
{
    /// <summary><c>ce-id</c>: the id of the event it publishes; the same for every contract version of that event.</summary>
    public required Guid Id { get; init; }
    /// <summary><c>ce-source</c>: the publishing system, e.g. <c>artflow</c>. Informational: the consumer checks it against the contract's prefix.</summary>
    public required string Source { get; init; }
    /// <summary><c>ce-type</c>: the contract, prefixed by the owning system, e.g. <c>artflow.auction-settled</c>. Also the topic.</summary>
    public required string Type { get; init; }
    /// <summary><c>ce-contractversion</c> (extension).</summary>
    public required int Version { get; init; }
    /// <summary><c>ce-time</c>: when the event was recorded.</summary>
    public required DateTimeOffset Time { get; init; }
    /// <summary><c>ce-subject</c>: the stream id (the ordering key).</summary>
    public string? Subject { get; init; }
    /// <summary><c>ce-tenant</c> (extension).</summary>
    public Option<Id<ITenant>> Tenant { get; init; }
    /// <summary><c>ce-correlation</c> (extension).</summary>
    public required Id<ICorrelation> Correlation { get; init; }
    /// <summary><c>ce-causation</c> (extension).</summary>
    public Option<Id<ICausation>> Causation { get; init; }
    /// <summary><c>ce-actor</c> (extension): who acted in the publishing system, <c>Kind:id</c>. Recorded, never trusted.</summary>
    public string? Actor { get; init; }
    /// <summary><c>traceparent</c> (CloudEvents distributed-tracing extension).</summary>
    public string? Traceparent { get; init; }
    /// <summary>The contract's JSON (<c>content-type: application/json</c>).</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The id a broker deduplicates by: one per (event, contract, version). The event id alone is not unique
    /// per stream when one event is published under two versions.</summary>
    public string DedupId => $"{Id:N}:{Type}@{Version}";

    /// <summary>The message of a committed event.</summary>
    public static Message Of(RecordedEvent recorded, string source, string type, int version, byte[] data, string? traceparent)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        var actor = recorded.Context.Actor;
        return new Message
        {
            Id = recorded.EventId, Source = source, Type = type, Version = version, Time = recorded.RecordedAt,
            Subject = recorded.StreamId.ToString("N", CultureInfo.InvariantCulture), Tenant = recorded.Context.Tenant,
            Correlation = recorded.Context.CorrelationId, Causation = (Id<ICausation>)recorded.EventId,
            Actor = actor.OnBehalfOf.TryGetValue(out string? behalf) ? $"{actor.Kind}:{actor.Id}:{behalf}" : $"{actor.Kind}:{actor.Id}",
            Traceparent = traceparent, Data = data,
        };
    }

    /// <summary>The headers of binary content mode.</summary>
    public IEnumerable<KeyValuePair<string, string>> Headers()
    {
        yield return new("ce-specversion", "1.0");
        yield return new("ce-id", Id.ToString("D", CultureInfo.InvariantCulture));
        yield return new("ce-source", Source);
        yield return new("ce-type", Type);
        yield return new("ce-contractversion", Version.ToString(CultureInfo.InvariantCulture));
        yield return new("ce-time", Time.ToString("O", CultureInfo.InvariantCulture));
        yield return new("content-type", "application/json");
        if (Subject is not null) yield return new("ce-subject", Subject);
        if (Tenant.TryGetValue(out Id<ITenant> tenant)) yield return new("ce-tenant", tenant.Value.ToString("D", CultureInfo.InvariantCulture));
        yield return new("ce-correlation", Correlation.Value.ToString("D", CultureInfo.InvariantCulture));
        if (Causation.TryGetValue(out Id<ICausation> causation)) yield return new("ce-causation", causation.Value.ToString("D", CultureInfo.InvariantCulture));
        if (Actor is not null) yield return new("ce-actor", Actor);
        if (Traceparent is not null) yield return new("traceparent", Traceparent);
    }

    /// <summary>Reads the headers back; <see langword="null"/> when a required one is missing or malformed (a poison message).</summary>
    public static Message? From(Func<string, string?> header, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header("ce-specversion") != "1.0"
            || !Guid.TryParse(header("ce-id"), out var id)
            || header("ce-source") is not { Length: > 0 } source
            || header("ce-type") is not { Length: > 0 } type
            || !int.TryParse(header("ce-contractversion"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !DateTimeOffset.TryParse(header("ce-time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            || !Guid.TryParse(header("ce-correlation"), out var correlation))
            return null;
        Option<Id<ITenant>> tenant = Option.None;
        if (header("ce-tenant") is { } t)
        {
            if (!Guid.TryParse(t, out var tg)) return null;
            tenant = (Id<ITenant>)tg;
        }
        Option<Id<ICausation>> causation = Option.None;
        if (header("ce-causation") is { } c && Guid.TryParse(c, out var cg))
            causation = (Id<ICausation>)cg;
        return new Message
        {
            Id = id, Source = source, Type = type, Version = version, Time = time, Subject = header("ce-subject"), Tenant = tenant,
            Correlation = (Id<ICorrelation>)correlation, Causation = causation, Actor = header("ce-actor"), Traceparent = header("traceparent"), Data = data,
        };
    }
}
