using System.Globalization;
using Tarol.Kernel;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Messaging;

/// <summary>
/// One message between systems: CloudEvents 1.0 in <b>binary content mode</b> (M-5). The attributes travel as headers
/// (<see cref="Headers"/>), the body is the contract's JSON (<see cref="Data"/>).
/// </summary>
/// <remarks>
/// The contract (<see cref="Type"/>) is prefixed by the system that owns it (<c>billing.invoice-paid</c>); that prefix is the
/// source a consumer trusts, enforced by the transport's permissions (M-13). <see cref="Tenant"/>, <see cref="Actor"/> and
/// <see cref="Source"/> are claims a consumer checks; see <c>docs/architecture/messaging.md</c> §5.
/// </remarks>
public sealed record Message
{
    /// <summary><c>ce-id</c>: the id of the event it publishes, the same for every contract version of that event.</summary>
    public required Guid Id { get; init; }

    /// <summary><c>ce-source</c>: the publishing system, e.g. <c>artflow</c>; must equal the contract's prefix.</summary>
    public required string Source { get; init; }

    /// <summary><c>ce-type</c>: the contract, e.g. <c>artflow.auction-settled</c>; also the topic.</summary>
    public required string Type { get; init; }

    /// <summary><c>ce-contractversion</c> (extension): the contract version, 1 or more.</summary>
    public required int Version { get; init; }

    /// <summary><c>ce-time</c>: when the event was recorded.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary><c>ce-subject</c>: the stream id, the ordering key; <see langword="null"/> when there is none.</summary>
    public string? Subject { get; init; }

    /// <summary><c>ce-tenant</c> (extension): the tenant the publisher acted in.</summary>
    public Option<Id<ITenant>> Tenant { get; init; }

    /// <summary><c>ce-correlation</c> (extension): the conversation.</summary>
    public required Id<ICorrelation> Correlation { get; init; }

    /// <summary><c>ce-causation</c> (extension): what caused the event at the publisher.</summary>
    public Option<Id<ICausation>> Causation { get; init; }

    /// <summary><c>ce-actor</c> (extension): who acted at the publisher, <c>Kind:id</c> or <c>Kind:id:onBehalfOf</c>. Recorded, never trusted.</summary>
    public string? Actor { get; init; }

    /// <summary><c>traceparent</c> (CloudEvents distributed-tracing extension): the publishing span, linked by the consumer.</summary>
    public string? Traceparent { get; init; }

    /// <summary>The contract's JSON (<c>content-type: application/json</c>).</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// The id a broker deduplicates by: one per (event, contract, version). The event id alone is not unique on a broker
    /// that deduplicates per stream when one event is published under two contract versions.
    /// </summary>
    public string DedupId => Id.ToString("N", CultureInfo.InvariantCulture) + ":" + Type + "@" + Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>The identity in <see cref="Actor"/> a consumer records as <c>OnBehalfOf</c>: the original subject (the third part, else the second).</summary>
    public string? ActorSubject
    {
        get
        {
            if (Actor is not { Length: > 0 } actor)
                return null;
            var parts = actor.Split(':', 3);
            return parts.Length switch
            {
                3 when parts[2].Length > 0 => parts[2],
                >= 2 when parts[1].Length > 0 => parts[1],
                _ => null,
            };
        }
    }

    /// <summary>The headers of binary content mode, in a fixed order.</summary>
    /// <returns>Name and value of each header the message carries.</returns>
    public IEnumerable<KeyValuePair<string, string>> Headers()
    {
        yield return new(MessageHeaders.SpecVersion, "1.0");
        yield return new(MessageHeaders.Id, Id.ToString("D", CultureInfo.InvariantCulture));
        yield return new(MessageHeaders.Source, Source);
        yield return new(MessageHeaders.Type, Type);
        yield return new(MessageHeaders.ContractVersion, Version.ToString(CultureInfo.InvariantCulture));
        yield return new(MessageHeaders.Time, Time.ToString("O", CultureInfo.InvariantCulture));
        yield return new(MessageHeaders.ContentType, "application/json");
        if (Subject is not null)
            yield return new(MessageHeaders.Subject, Subject);
        if (Tenant.TryGetValue(out Id<ITenant> tenant))
            yield return new(MessageHeaders.Tenant, tenant.Value.ToString("D", CultureInfo.InvariantCulture));
        yield return new(MessageHeaders.Correlation, Correlation.Value.ToString("D", CultureInfo.InvariantCulture));
        if (Causation.TryGetValue(out Id<ICausation> causation))
            yield return new(MessageHeaders.Causation, causation.Value.ToString("D", CultureInfo.InvariantCulture));
        if (Actor is not null)
            yield return new(MessageHeaders.Actor, Actor);
        if (Traceparent is not null)
            yield return new(MessageHeaders.Traceparent, Traceparent);
    }

    /// <summary>Reads a message from its headers; <see langword="null"/> when a required one is missing or malformed (a poison message).</summary>
    /// <param name="header">The header of a name, or <see langword="null"/>.</param>
    /// <param name="data">The body.</param>
    /// <returns>The message, or <see langword="null"/>.</returns>
    public static Message? From(Func<string, string?> header, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header(MessageHeaders.SpecVersion) != "1.0"
            || !Guid.TryParse(header(MessageHeaders.Id), out var id)
            || header(MessageHeaders.Source) is not { Length: > 0 } source
            || header(MessageHeaders.Type) is not { Length: > 0 } type
            || !int.TryParse(header(MessageHeaders.ContractVersion), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version < 1
            || !DateTimeOffset.TryParse(header(MessageHeaders.Time), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            || !Guid.TryParse(header(MessageHeaders.Correlation), out var correlation))
            return null;
        Option<Id<ITenant>> tenant = Option.None;
        if (header(MessageHeaders.Tenant) is { } tenantText)
        {
            if (!Guid.TryParse(tenantText, out var tenantId))
                return null;
            tenant = (Id<ITenant>)tenantId;
        }
        Option<Id<ICausation>> causation = Option.None;
        if (header(MessageHeaders.Causation) is { } causationText)
        {
            if (!Guid.TryParse(causationText, out var causationId))
                return null;
            causation = (Id<ICausation>)causationId;
        }
        return new Message
        {
            Id = id,
            Source = source,
            Type = type,
            Version = version,
            Time = time,
            Subject = header(MessageHeaders.Subject),
            Tenant = tenant,
            Correlation = (Id<ICorrelation>)correlation,
            Causation = causation,
            Actor = header(MessageHeaders.Actor),
            Traceparent = header(MessageHeaders.Traceparent),
            Data = data,
        };
    }

    /// <summary>
    /// The message of a committed event for one publication: id = the event id (the same for every contract version, so a
    /// consumer of both commits once), source = the contract's prefix, time = when it was recorded, subject = the stream id,
    /// tenant, correlation and causation of the event's operation, actor <c>Kind:id[:onBehalfOf]</c>.
    /// </summary>
    /// <param name="recorded">The committed event.</param>
    /// <param name="contract">The contract, prefixed by this system.</param>
    /// <param name="version">The contract version.</param>
    /// <param name="data">The contract's JSON.</param>
    /// <param name="traceparent">The publishing activity (W3C), or <see langword="null"/>.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentException">The contract has no system prefix, or the version is below 1.</exception>
    public static Message Of(RecordedEvent recorded, string contract, int version, ReadOnlyMemory<byte> data, string? traceparent)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(contract);
        var source = SystemOf(contract);
        if (source.Length == 0)
            throw new ArgumentException($"The contract '{contract}' has no system prefix (<system>.<name>).", nameof(contract));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        var context = recorded.Context;
        var actor = context.Actor;
        return new Message
        {
            Id = recorded.EventId,
            Source = source,
            Type = contract,
            Version = version,
            Time = recorded.RecordedAt,
            Subject = recorded.StreamId.ToString("D", CultureInfo.InvariantCulture),
            Tenant = context.Tenant,
            Correlation = context.CorrelationId,
            Causation = context.CausationId,
            Actor = actor.OnBehalfOf.TryGetValue(out string? behalf) ? $"{actor.Kind}:{actor.Id}:{behalf}" : $"{actor.Kind}:{actor.Id}",
            Traceparent = traceparent,
            Data = data,
        };
    }

    /// <summary>The system that owns a contract: its prefix before the first dot; empty when there is none.</summary>
    /// <param name="contract">A contract name such as <c>billing.invoice-paid</c>.</param>
    /// <returns>The system, e.g. <c>billing</c>.</returns>
    public static string SystemOf(string contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var dot = contract.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? contract[..dot] : "";
    }
}

/// <summary>The header names of CloudEvents' binary content mode as Tarol writes them (transports may map them, e.g. AMQP's <c>cloudEvents:</c> prefix).</summary>
public static class MessageHeaders
{
    /// <summary><c>ce-specversion</c>.</summary>
    public const string SpecVersion = "ce-specversion";
    /// <summary><c>ce-id</c>.</summary>
    public const string Id = "ce-id";
    /// <summary><c>ce-source</c>.</summary>
    public const string Source = "ce-source";
    /// <summary><c>ce-type</c>.</summary>
    public const string Type = "ce-type";
    /// <summary><c>ce-contractversion</c>.</summary>
    public const string ContractVersion = "ce-contractversion";
    /// <summary><c>ce-time</c>.</summary>
    public const string Time = "ce-time";
    /// <summary><c>content-type</c>.</summary>
    public const string ContentType = "content-type";
    /// <summary><c>ce-subject</c>.</summary>
    public const string Subject = "ce-subject";
    /// <summary><c>ce-tenant</c>.</summary>
    public const string Tenant = "ce-tenant";
    /// <summary><c>ce-correlation</c>.</summary>
    public const string Correlation = "ce-correlation";
    /// <summary><c>ce-causation</c>.</summary>
    public const string Causation = "ce-causation";
    /// <summary><c>ce-actor</c>.</summary>
    public const string Actor = "ce-actor";
    /// <summary><c>traceparent</c>.</summary>
    public const string Traceparent = "traceparent";
}
