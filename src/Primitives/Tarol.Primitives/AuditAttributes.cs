namespace Tarol.Primitives;

/// <summary>
/// Leaves an event member, or a whole event, out of the change log of <c>Tarol.Audit</c> (TN-64, owner decision AU-2: every member of
/// every event is audited unless marked). Give a reason: the log is evidence, and an unaudited member needs one.
/// </summary>
/// <example><code>public sealed record Registered(string Name, [property: NotAudited(Reason = "a checksum, no fact")] string Checksum) : Event;</code></example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class NotAuditedAttribute : Attribute
{
    /// <summary>Why the member or event is not audited.</summary>
    public string? Reason { get; init; }
}

/// <summary>Marks an event that removes its object (an entity or the stream): the audit writes a <c>removed</c> row (TN-64).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuditRemovedAttribute : Attribute;

/// <summary>
/// The member of an event that holds when the fact happened on the device (a <see cref="DateTimeOffset"/>, owner decision AU-3 (b)):
/// the audit row's <c>capturedAt</c>. Without it the row takes the operation's capture time (<c>Tarol-Captured-At</c>), if any.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class AuditTimeAttribute : Attribute;

/// <summary>An event member named in an <see cref="EventAudit"/>: its CLR name and its explicit JSON name, if any.</summary>
/// <param name="Name">The member name.</param>
/// <param name="JsonName">The <c>[JsonPropertyName]</c>, or <see langword="null"/> for the serializer's naming policy.</param>
public sealed record AuditMember(string Name, string? JsonName = null);

/// <summary>How an event is audited (TN-64), generated into <see cref="EventCatalogEntry.Audit"/> when it differs from "every member".</summary>
/// <param name="Excluded">The event is <see cref="NotAuditedAttribute">[NotAudited]</see>.</param>
/// <param name="Removed">The event is <see cref="AuditRemovedAttribute">[AuditRemoved]</see>.</param>
/// <param name="NotAudited">The members left out.</param>
/// <param name="Time">The <see cref="AuditTimeAttribute">[AuditTime]</see> member, if any (it is not a field of the log).</param>
public sealed record EventAudit(bool Excluded, bool Removed, IReadOnlyList<AuditMember> NotAudited, AuditMember? Time = null);
